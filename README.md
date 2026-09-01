<p align="center">
  <img src="installer/clarion-assistant-256.png" alt="Clarion Assistant" width="128" height="128">
</p>

<h1 align="center">Clarion Assistant</h1>

<p align="center">
  <strong>AI-powered coding assistant for the Clarion IDE</strong><br>
  Embeds Claude Code directly into your Clarion development workflow
</p>

<p align="center">
  <a href="https://github.com/ClarionLive/ClarionAssistant/releases/latest"><img src="https://img.shields.io/github/v/release/ClarionLive/ClarionAssistant?include_prereleases&label=download&style=for-the-badge" alt="Download"></a>
  <img src="https://img.shields.io/badge/Clarion-10%20%7C%2011%20%7C%2011.1%20%7C%2012-blue?style=for-the-badge" alt="Clarion 10 | 11 | 11.1 | 12">
  <img src="https://img.shields.io/badge/version-5.5-blue?style=for-the-badge" alt="v5.5">
</p>

<p align="center">
  <em>An independent community project &mdash; not a SoftVelocity product.</em><br>
  <a href="docs/HISTORY.md">History, stats &amp; contributors</a>
</p>

---

## What is Clarion Assistant?

Clarion Assistant is an IDE addin that brings AI-powered code intelligence to [Clarion](https://softvelocity.com) developers. It runs as a docked terminal pane inside the Clarion IDE, giving you a conversational coding assistant that understands your entire codebase.

Ask it to write Clarion code, explain procedures, refactor classes, build COM controls, convert Clarion apps to C#, or navigate your solution &mdash; all without leaving the IDE.

### Key Capabilities

- **Write and edit Clarion code** directly in the IDE editor
- **Multi-tab terminal** &mdash; multiple Claude Code sessions with independent workspaces
- **Language Server (LSP)** &mdash; real-time code intelligence with go-to-definition, find references, hover info, diagnostics, and rename support
- **CodeGraph** &mdash; solution-wide code intelligence via SQL queries over every symbol, relationship, and call chain
- **DocGraph** &mdash; instant search across 14,000+ indexed documentation chunks (Clarion core, CapeSoft, Icetips, and more)
- **SchemaGraph** &mdash; database schema intelligence from Clarion dictionaries, SQL Server, SQLite, and PostgreSQL
- **Source Control** &mdash; GitHub and Bitbucket integration with per-solution repo linking
- **Build tools** &mdash; build solutions, individual apps, or C# COM controls without leaving the chat
- **Class intelligence** &mdash; parse CLASS definitions, sync .inc/.clw, generate method stubs
- **Application tree** &mdash; open .app files, list procedures, navigate the embeditor
- **Monaco source editor** &mdash; the default editor for Clarion `.clw`/`.inc` source: syntax highlighting, folding, F12/Ctrl+Click go-to-definition, inline diagnostics, and completion (toggleable under Options &rarr; Clarion Assistant &rarr; Editor Surfaces)
- **Smart Formatter (Ctrl+I)** &mdash; reformats Clarion code with structure indentation and aligned declarations; configurable
- **CA Embeditor** &mdash; use Clarion's own **Embeditor Source** (right-click a procedure, or the Views toolbar button) and a fast Monaco/WebView2 editor overlays the native embeditor automatically; edits save straight back with Clarion-native Save &amp; Exit
- **Embed navigation (Ctrl+J / Ctrl+B)** &mdash; jump to the next/previous *filled* embed, the same keys the native Clarion embeditor uses (#185); the toolbar arrows and an unfiltered walk over every embed are there too, and all of it is rebindable
- **Code Snippets (Ctrl+Shift+J)** &mdash; classic Clarion template-picker parity: insert reusable code with tab-stops and a `${SELECTED}` placeholder, managed from Settings &rarr; Snippets
- **CA Explorer** &mdash; docked pad showing the active CA Embeditor tab's Local, Module &amp; Global Data, Declared Tables, Other Files, and their Keys, Columns, and Relations; drag a field to the editor or Window designer, copy/paste variables native-style, and a Cheat Sheet tab of editor shortcuts
- **Evaluate Code** &mdash; interactive code review for entire apps, procedures, open files, or selected code
- **CA Find & Replace** &mdash; dockable Find pad or classic in-editor overlay (your pick), Find-All with results in their own editor tab, and one shared history across every CA surface
- **Document Structure** &mdash; fly-out outline of the current buffer with symbol icons, Class &#9656; Methods regrouping, and filtering; click to navigate
- **Diff viewer** &mdash; Monaco-based side-by-side diffs with syntax highlighting, live-buffer diffing, and a current-line comparison panel
- **Knowledge system** &mdash; persistent cross-session memory for decisions, patterns, and gotchas
- **Zoom persistence** &mdash; Ctrl+mousewheel zoom is saved and restored across sessions

---

## What's New (Unreleased)

> **DRAFT for 5.9.0.** Written by Charlie for John to edit. Coverage is complete against every commit since v5.8.1; the voice is not final.

**Why 5.9.0 and not 5.8.2.** Clarion Assistant is joining the **Clarion Addin Registry**, so it can be found and updated from **AddinFinder** inside the IDE. That required our version number to become a single value that the addin manifest, the installer and the git tag all agree on &mdash; and it could not be 5.8.2. See the versioning entry below.

<!-- release-docs: covered=mcp,mcp-server,codegraph -->
### Clarion Assistant's tools now run without the Clarion IDE

There is a new **`clarion-mcp-server`**: the editor-agnostic half of Clarion Assistant, as a standalone program that speaks **MCP over stdio**. Point any MCP client at it &mdash; Claude Code in **Sublime Text**, in **VS Code**, or in a plain terminal &mdash; and you get Clarion intelligence in an editor that is not the IDE. This is for Clarion developers who hand-code and would rather not work in the Clarion IDE at all.

```
clarion-mcp-server --stdio --solution C:\Path\To\Your.sln
```

It serves **57 of the 115 tools**: the documentation search across SoftVelocity and every third-party vendor you have installed, the knowledge base, the LSP tools, the dictionary and SQL schema tools, CodeGraph indexing and queries, file and Everything search, and Clarion class analysis. Indexing works fully &mdash; it reads your `.red` redirection file the way the compiler does, so a solution with one hand-written source file still indexes the ABC library behind it.

The other **58 are withheld on purpose**, because they drive the IDE itself: opening files in the editor, the app tree, the embeditor, the designer. An MCP client reads the tool list as a promise about what it can do, so a tool that could only ever fail is worse than one that is honestly absent. The addin is unchanged and still offers all 115.

**Both can be running at once.** If your IDE and a standalone server both index the same solution, they no longer collide: a full re-index wipes the database before rebuilding it, so two overlapping runs used to be able to destroy each other's work and leave a graph pointing at code that had been deleted from it. Whichever starts second is now turned away, and told which process holds the database. A run whose process is killed &mdash; a deploy, a crash, Task Manager &mdash; releases immediately and leaves nothing stale behind.

### CA Embeditor: the keys that used to do nothing now reach the IDE ([#192](https://github.com/ClarionLive/ClarionAssistant/issues/192))

Inside a CA Embeditor the editor swallowed most of Clarion's own shortcuts. **Ctrl+F4** (close), **Ctrl+O** (open) and **Alt+&lt;letter&gt;** now reach the IDE, the last of these by matching the letter against the real menu mnemonics rather than a hardcoded list &mdash; so it keeps working if the menus change. Keys are routed through the workbench menu itself, which means the IDE does what it would normally do rather than us reimplementing it.

### CA Embeditor: Ctrl+F4 on unsaved work now asks, because Clarion asks

Closing a dirty embeditor with Ctrl+F4 discarded the edits **silently**. The CA Editor prompted and the embeditor did not, which made it look arbitrary. The prompt was never broken: Clarion raises *"Save Changes in Embed Editor?"* by consulting the **native** editor's dirty flag, and our overlay never set it, so Clarion saw a clean embed and closed. We now flush the Monaco edits into the native embed and set that flag *before* the close is dispatched, so **Clarion raises its own dialog** &mdash; exact parity by construction, nothing imitated. Answering **Yes** saves the edits you can see rather than the stale native buffer underneath, which would have been a quieter and worse bug than the missing prompt. The toolbar's **red X** asks too, matched word for word to Clarion's dialog, and answering **Cancel** leaves you editing with your work still protected.

### CA Embeditor: an orphaned overlay is now torn down for real

When the native embed closed underneath the overlay &mdash; Errors-pane navigation opening the module source, a native cancel, an app-gen regeneration &mdash; the overlay kept its text and still looked healthy, while Save reported *"nothing to save"* and Cancel blanked the buffer. A teardown hook existed for exactly this and had **never once fired**: it subscribed to a `Disposed` event that does not exist on that class in this Clarion fork, and the null check meant to guard the subscription swallowed the failure, so the safety net read as present in the source while being connected to nothing.

### Ctrl+Q now shows Clarion's own confirmation, not ours ([#193](https://github.com/ClarionLive/ClarionAssistant/issues/193))

The Ctrl+Q confirmation was a web-styled dialog that looked nothing like the rest of the IDE. It is now the **native Windows dialog**, and it is scoped to the embeditor rather than firing globally.

<!-- release-docs: covered=installer -->
### Installing no longer corrupts non-ASCII characters in your Claude Code settings ([#200](https://github.com/ClarionLive/ClarionAssistant/issues/200))

A follow-on to [#190](https://github.com/ClarionLive/ClarionAssistant/issues/190), and the same root cause: the installer's configuration step runs under **Windows PowerShell 5.1**, where two defaults are not what they appear. Reading `settings.json` without naming an encoding decoded it as the machine's **ANSI codepage**, so every non-ASCII character was mangled on the way *in* and written back mangled &mdash; a typographic apostrophe became `â€™`. Writing it back with `-Encoding UTF8` added a **byte-order mark** to a file that belongs to Claude Code, because that same token means *with* BOM on 5.1 and *without* on PowerShell 7. And a **successful** merge kept no backup at all: a copy was only ever written when the installer *refused* to write, which is why the 5.8 notes pointed people at a backup file the normal path never produced. All three are fixed, a backup is now written before the file is overwritten, and an already-BOM'd file from 5.8 is read correctly and cleaned. **Characters already mangled on disk cannot be recovered** &mdash; the original bytes are gone. The installer's configuration script is now covered by a regression test that runs it under a real 5.1 host and checks the result byte by byte.

### Version numbers now mean one thing ([#200](https://github.com/ClarionLive/ClarionAssistant/issues/200) groundwork for AddinFinder)

The released version is now **Major.Minor.Patch** &mdash; one number, shared by `Version.props`, the addin manifest and the git tag. Previously the third component was a build counter that incremented on **every compile**, so the manifest said `5.8.1165` while the release was tagged `v5.8.1` and the installer carried a third, hand-maintained `5.8.1`. AddinFinder compares the manifest against the release tag, so any disagreement shows as *"Update available"* permanently, and reinstalling cannot clear it.

**This is why the jump is to 5.9.0 rather than 5.8.2.** AddinFinder only ever lets a recorded version move *forward*, and it compares component by component &mdash; so against a manifest already reporting `5.8.1165`, the number `5.8.2` compares as **lower** (2 against 1165) and would have left every existing user stuck on that message forever, while looking perfectly correct on any fresh install. The build counter has not gone away: it still increments and still appears in the DLL's file properties and in the docked pad's title (`5.9.0.1165`), where it is useful for support.

### The About box now shows the full version number

The About box reported `5.9` where every other surface reports `5.9.0.1165`. It built the string from the assembly version's **Major and Minor only**, dropping the patch and the build counter &mdash; the two components that actually distinguish one install from another. It now shows all four, which matters mostly for support: the build counter is how you tell a deployed build apart from the one before it, and reading it off the About box is the quickest way to get it.

### The VS Code import preview no longer promises a font it cannot deliver

Importing settings from VS Code previews what will change. Where **Follow Clarion's editor options** is ticked some of those values are owned by the IDE rather than the pad, and the preview already warned about that on the indentation rows &mdash; but not on **Font size** and **Font family**, which are follow-mode-owned too. So the preview offered a font change, the import stored it correctly, and the editor carried on showing the IDE's font, leaving the setting looking silently ignored; unticking follow-mode reveals the imported value was there the whole time. The warning now appears on the font rows as well, and only when the IDE is actually reporting a font, so it never warns about a setting that would in fact have applied.

### Bundled skills: the marketplace ones said .NET 4.8, the templates build 4.7.2

The ClarionCOM skills that ship with Clarion Assistant told you your project targets **net48**. Both shipped templates target **net472** &mdash; `ClarionCOMTemplate.csproj` and `WebView2Template.csproj` alike. That is not a label being out by a decimal point: the deployment skill sent you to copy build output from `bin/Release/net48/`, **a path MSBuild never writes**, and the control checklist would fail a correctly configured project. Corrected throughout the four affected skills.

The blanket rule against generating Clarion code was also too broad, and is narrowed from *"never generate Clarion code examples"* to *"never hand-write per-control examples"*. The reason it needed narrowing: the Clarion templates cover the **inbound** half only &mdash; `UltimateCOM.tpl` and the `UltimateCOM` class handle instantiation, placement and events &mdash; while every outbound method call and data payload is hand-written by the developer with no guidance anywhere in the product. A fixed, reviewed calling-convention block now fills exactly that gap, checked against the SoftVelocity Help topics *"Parameter Passing to OLE/OCX Methods"* and *"Calling OLE Object Methods"*. The original concern, that improvised per-control Clarion would likely be wrong, is preserved and still enforced.

### Thanks

- **[@BoxSoft](https://github.com/BoxSoft)** &mdash; [#192](https://github.com/ClarionLive/ClarionAssistant/issues/192) and [#193](https://github.com/ClarionLive/ClarionAssistant/issues/193). Both reports named the specific keys and the specific visual mismatch, which is what made them fixable rather than a general complaint about feel.
- **[@KevinErskine](https://github.com/KevinErskine)** &mdash; [#200](https://github.com/ClarionLive/ClarionAssistant/issues/200), and for the second time a gold-vs-live pair of his settings file. One character differed, and having both copies turned "something changed" into a measurable byte sequence. He also answered the follow-up question that ruled out a fourth defect.
- **[Mark Sarson](https://github.com/msarson)** &mdash; for the Clarion Addin Registry and AddinFinder, whose source settled how our version numbers have to behave.

---

## What's New in v5.8.1

A patch release that fixes something 5.8 broke. Full notes: **[docs/releases/v5.8.1.md](docs/releases/v5.8.1.md)**.

<!-- release-docs: covered=installer -->
### Clarion starts again after installing the Markdown editor

Installing 5.8 could leave `accessory\addins\MarkdownEditor` holding **exactly one file** &mdash; the `.addin` manifest &mdash; and none of the assemblies it names. Clarion reads that manifest at startup, fails to load the DLL beside it, and **stops with two dialogs instead of opening**. The editor's files ship as one wildcard entry, and Inno Setup evaluates such an entry's install check *once per expanded file*; the manifest sorts alphabetically first, so it was written, and every remaining file then re-ran the check, found the manifest just written reporting the version being installed, took the "you already have this" branch and was skipped. The gate destroyed its own precondition. Reinstalling did not help &mdash; that lone manifest kept reporting the current version, so the broken state was exactly the state the repair logic refused to repair. The decision is now made once per Clarion version and frozen before the first write, and a manifest with no assembly beside it is treated as damage rather than as an install, which is what **repairs already-broken machines in place**. A copy of the editor newer than the bundled one is still left alone.

### Markdown editor v1.3.0

The bundled editor moves to **[v1.3.0](https://github.com/msarson/ClarionMarkdownEditor/releases/tag/v1.3.0)** &mdash; auto-refresh, remembered view preferences, and resizable panes.

> The Clarion Assistant addin itself is unchanged from 5.8: same binaries, same version stamp. Only the installer's logic and the Markdown editor it carries are different.

### Thanks

- The user who reported this on **Discord**, with both dialogs captured. The screenshots named the file and the path, which is what separated "the DLL is missing" from "the DLL cannot load" &mdash; very different bugs.

---

## What's New in v5.8

5.8 is the CodeGraph release &mdash; and one apology. Full notes: **[docs/releases/v5.8.0.md](docs/releases/v5.8.0.md)**.

> **Re-index your solutions and re-import your documentation after updating.** This release corrects what gets *read*, not what is already stored.

### Installing no longer wipes your Claude Code settings ([#190](https://github.com/ClarionLive/ClarionAssistant/issues/190))

Every install, on every machine, overwrote `%USERPROFILE%\.claude\settings.json` &mdash; Claude Code's own global configuration &mdash; leaving only the handful of keys the installer itself writes. `hooks`, `statusLine`, `model`, `tui`, your plugins and your own `permissions.allow` entries were gone, and losing `hooks` is the worst of it because nothing announces it. The installer runs its configuration step under Windows PowerShell 5.1 and the script asked for a JSON option that only exists in PowerShell 6+, so the parse failed every time &mdash; and the error handler mistook its own unsupported call for a corrupt user file, backed it up, and rebuilt from empty. **If this hit you, your settings are still on disk:** look next to the file for `settings.json.backup.` plus a timestamp. The parse works on both hosts now, a genuine failure leaves your file alone, a guard refuses any write that would drop a top-level key, and the installer build fails if any of its scripts would not load under real 5.1.

### CodeGraph: thirty times faster, and no longer confidently wrong

A full index of a 27-app production solution fell from **1:12 to 2:42**, with the output verified identical row by row. The correctness half matters more: "who calls X" could answer with the wrong X entirely, because every call in every app resolved to one arbitrary copy of a shared procedure name. Resolution is now scoped the way the compiler thinks, genuinely ambiguous picks are **marked** rather than asserted, and prototypes are told apart from implementations &mdash; which also fixes a documented dead-code query that was returning **98.7% false positives**.

Three whole categories of code had been invisible. **Procedures whose labels contain a colon** were never indexed at all &mdash; in generated Clarion that is the entire referential-integrity layer, so "what breaks if I delete from this table" returned nothing. **Routine bodies** were never scanned, because a `ROUTINE` label switched the scanner off and `DO ProcedureReturn` prefix-matched the PROCEDURE pattern. And **global data** &mdash; your PROGRAM file's declaration section &mdash; was skipped entirely, with references to an imported global landing on the importing app's copy instead of the declaration you navigate to. The test solution went from 478 thousand relationships to **1.1 million**.

### Indexing shows its work, and the tools stream it

Starting an index opens a **progress window**: apps ticked off as they parse, the file being read, a bar weighted by where the time actually goes, and an estimate seeded from your last run. It can be **cancelled** &mdash; a cancelled full index deletes the partial database rather than leaving something that passes for complete. The transcript is always written to `%APPDATA%\ClarionAssistant\codegraph-index.log`, and the window no longer steals focus from the IDE. Over MCP, `index_solution` and `index_codegraph` now stream live progress and return real completion stats instead of an hour of silence.

### Asking the assistant to build compiles what is on your screen

The assistant's build tools shelled straight out to `ClarionCL` without entering the IDE's build pipeline, so the hook that saves unsaved CA Editor tabs never ran &mdash; the toolbar button saved them, the assistant did not, and you got the stale build. Alongside it: **saving no longer throws the caret to line 1** (our own write looked like an external change to the native editor underneath, whose caret reset was then faithfully mirrored into view), and the editor no longer keeps its unsaved-changes dot on a file it has just saved. **`.tpl` and `.tpw`** listed in Editor Surfaces finally open in the editor, and writing an entry as `*.tpl` no longer produces a pattern that silently matches nothing.

### Markdown editor, embeditor, and per-environment history

Mark Sarson's **[Markdown editor](https://github.com/msarson/ClarionMarkdownEditor)** now ships in the installer, pinned like the bundled language server, and is left alone if you already have a newer copy. The **CA Embeditor** attaches in colon-named procedure suites ([#196](https://github.com/ClarionLive/ClarionAssistant/issues/196)) &mdash; on one reporter's application it had never attached once in six weeks. Two Clarion environments started with `/ConfigDir=` no longer **share one application history** ([#197](https://github.com/ClarionLive/ClarionAssistant/issues/197)); CA had been rebuilding the path from the executable's version stamp, and Clarion 11 and 11.1 both report `11.0`. Migration-free &mdash; a default install resolves to exactly the string it did before.

### Also fixed

**Documentation search stops mangling accented characters** &mdash; the ingester read UTF-8 documents as the machine's ANSI codepage, and the wrong encoding was passed *explicitly*, which is how it survived two previous sweeps. The **embedded assistant knows about every tool it has** in the copy that actually ships: 5.7 went out with a prompt missing 51 registered tools, because the fix had landed in a file that gets overwritten on every terminal start. A release **could ship with no language server** and say so in one grey line among thirty green ones. And CA terminals now **leave the MultiTerminal roster** when they close &mdash; three separate defects, the decisive one being that `localhost` stalled every call to its timeout, which had also left the Agents pad showing stale data.

### Thanks

- **[@KevinErskine](https://github.com/KevinErskine)** &mdash; [#190](https://github.com/ClarionLive/ClarionAssistant/issues/190), and the before-and-after copies of his settings file that made the damage measurable rather than inferred.
- **[@bill-atchison](https://github.com/bill-atchison)** &mdash; [#196](https://github.com/ClarionLive/ClarionAssistant/issues/196), reported with the root cause and a proposed fix, both of which held up against the source.
- **[@BoxSoft](https://github.com/BoxSoft)** &mdash; [#197](https://github.com/ClarionLive/ClarionAssistant/issues/197), and the dual-environment detail that explained why two Clarion versions collided on one identity.
- **[Mark Sarson](https://github.com/msarson)** &mdash; for the Markdown editor this release redistributes.

---

## What's New in v5.7

5.7 is a parity-and-reliability release. Full notes: **[docs/releases/v5.7.0.md](docs/releases/v5.7.0.md)**.

### Native embeditor parity &mdash; Ctrl+J / Ctrl+B

The CA Embeditor answers **Ctrl+J** (next filled embed) and **Ctrl+B** (previous) like the native one, wrapping at either end and acting on the focused split pane ([#185](https://github.com/ClarionLive/ClarionAssistant/issues/185), BoxSoft). The code-snippet picker moves to **Ctrl+Shift+J** &mdash; Ctrl+J is classic Clarion's snippet gesture in the *text* editor, but the *embeditor* owes it to embed navigation &mdash; and becomes rebindable like every other command, so it can be put back if you prefer. An unfiltered **Next/Previous Embed (any)** ships unbound.

### Errors-pane navigation survives opening a generated .clw

Clicking a row for one procedure after another row had opened the generated `.clw` appeared to do nothing. The reveal was always computing the right line &mdash; but the embeditor is a view *inside* the application window rather than a tab of its own, so raising it needed both levels, and opening the `.clw` closes the native embed underneath, leaving a surface where **Save** said "nothing to save" and **Cancel** blanked the buffer. Such a row now goes to Clarion's own navigation, which re-opens the embeditor properly.

### A language server call can no longer freeze the IDE

An embed save or cancel could hang the IDE for close to a minute &mdash; measured at 57.7s &mdash; waiting synchronously on an async language-server call from the UI thread. An audit found **twelve** such sites, not the two reported, so the pattern is fixed rather than one more symptom. Separately, an application **global** flagged `'X' is not declared in this file` while hovering correctly as a global is suppressed pending the upstream fix ([Clarion-Extension issue 396](https://github.com/msarson/Clarion-Extension/issues/396)).

### Community fixes

**Go-to-definition** stops resolving to an unrelated procedure's local variable ([#182](https://github.com/ClarionLive/ClarionAssistant/pull/182)) &mdash; a guard the hover path already used and the definition path never called. The **editor follows Clarion's live font** ([#183](https://github.com/ClarionLive/ClarionAssistant/pull/183)): it had been reading a property the Options dialog no longer writes to, so font changes never reached the editor. Both from [@geircodes](https://github.com/geircodes). Reviewing #183 turned up a way to lose your own font &mdash; with following on, any unrelated gear change persisted the IDE's font as your stored preference &mdash; fixed before release, along with the same shape in cursor-behind-EOL.

### Folds, encoding, search, installer

Collapsed **folds** are restored on reopen (the state saved but always read back empty), and an ambiguous drifted fold is refused rather than collapsing the wrong region. The Windows-1252 **encoding** sweep is finished &mdash; nineteen more reads, two of them read-modify-*write* &mdash; and reads no longer decode every file twice. **CA Search** opens in your theme instead of always dark ([#181](https://github.com/ClarionLive/ClarionAssistant/issues/181)). The **installer** checks that a folder's Clarion version matches the row it was entered in, and a row now accepts several folders for the same version via **`+`**, closing the gap 5.5's known issues warned about.

### Thanks

- **[@geircodes](https://github.com/geircodes)** &mdash; [#182](https://github.com/ClarionLive/ClarionAssistant/pull/182) and [#183](https://github.com/ClarionLive/ClarionAssistant/pull/183), with reproducers and live verification against a real IDE.
- **Adrián Santarelli** &mdash; the WebView2 post-after-dispose fix, reporting [#179](https://github.com/ClarionLive/ClarionAssistant/issues/179), and the original Ctrl+J snippet requests ([#49](https://github.com/ClarionLive/ClarionAssistant/issues/49), [#154](https://github.com/ClarionLive/ClarionAssistant/issues/154)).
- **BoxSoft** &mdash; [#185](https://github.com/ClarionLive/ClarionAssistant/issues/185), the embed-navigation hotkeys.

---

## What's New in v5.6

Documentation search is the headline: PDF text extraction now works on every machine instead of only ones that happened to have a third-party tool installed, the extracted text is more accurate, and it is indexed so that a question is answered by the first result rather than the fifth query. Alongside that, a cycle of fixes across the diagnostics path, completion scoping, and the CA Editor's Monaco overlay &mdash; plus a build fix that restores Clarion 10 to the shipped set.

<!-- release-docs: covered=docgraph -->
### PDF documentation actually imports &mdash; and is correct (#167)

Importing a folder of PDFs reported "No documentation files found", naming `pdf` as supported in the very message saying nothing was there. The files were found. Text extraction shelled out to an external `pdftotext.exe` that CA never bundled and nothing it requires installs &mdash; not Git for Windows, contrary to what the code's own probe paths assumed. So PDF import worked only on machines where a developer happened to have put one, and silently produced nothing everywhere else.

Extraction is now in-process (PdfPig, Apache-2.0), so it works everywhere with no external dependency.

The bigger surprise was accuracy. Where the old path *did* run, it misaligned multi-column tables: in the Language Reference's date-picture table it paired `@D6` (`dd/mm/yyyy`) with `10/1959` &mdash; which is `@D14`'s value, and cannot be a `dd/mm/yyyy` rendering of any date &mdash; while dropping other cells entirely. Every row now reads correctly. Those tables are exactly what a Clarion developer searches the documentation for, so the old path was not merely unavailable; where it ran, it was indexing wrong answers.

> **Re-import your own PDFs.** Anything already in a personal DocGraph was indexed through the old path and keeps the old text. The bundled documentation shipped with this release is already rebuilt.

### Documentation search answers the question, not the index (#167)

Extraction being correct is not the same as the answer being findable. Asking which three categories `ASCIIFileClass`'s non-virtual methods divide into took **five** queries; it now takes one, and the answer is the first result.

Four things were wrong at once. **Nothing identified the owning class** &mdash; every chunk in the ABC Library Reference was labelled with the book's name, and since every ABC class has an identically-named "Occasional Use" subsection, results from five different classes interleaved with nothing to tell them apart. **Table-of-contents pages outranked real content**: 28.7% of the index was dot-leader lines, which are almost pure keyword, so searching a class name returned page-number lists ahead of prose. **Clarion keywords lifted out of example code became headings** &mdash; 486 chunks titled `ACCEPT`, `PROGRAM` or `RETURN`, including the one holding the ASCIIFileClass text. And **subsection labels were splitting sections apart**, so the three categories landed in three different chunks and no single result could answer the question.

Chunks now carry their real class, contents pages rank below prose, headings read `ASCIIFileClass > GetLastLineNo`, and a section stays whole. Property references in the Language Reference (`PROP:NumTabs` and the rest) get their own headings too, so the definition outranks a passing mention in an example.

Verified against a fixed set of eight retrieval tests, kept with the code at [`ClarionAssistant/docs/DocGraph-Chunking-Verification.md`](ClarionAssistant/docs/DocGraph-Chunking-Verification.md), including a guard on the date-picture table above so a future chunking change cannot quietly undo the extraction fix.

Index-noise suppression currently covers documentation whose contents pages put the title and page number on one line &mdash; SoftVelocity's and CapeSoft's. BoxSoft's manuals wrap them across two lines and are not yet recognised.

### Spot which libraries need re-importing

The Documentation Graph panel (Settings &rarr; Data &rarr; Info) gains a **Type** column showing each library's source format, and every column header &mdash; Library, Type, Vendor, Chunks &mdash; is now a sort toggle. Click **Type** to group the PDFs together, which is the fastest way to see what wants a re-import after this release.

### The installer remembers where your Clarion actually is (#142)

Setup derived each Clarion path fresh on every run, registry first, and discarded whatever you corrected in the wizard. If your Clarion isn't where SoftVelocity's installer registered it &mdash; a second copy, or one launched with `/Configdir=` against its own settings folder &mdash; you had to re-enter the path on every release, and forget once.

That failure is quiet: the addin lands in a tree you don't launch, the IDE keeps loading the old one, and the symptoms get reported against a build replaced weeks ago. Paths a run actually installs to are now remembered and offered next time, and validated on read so a tree that has since moved falls back to detection.

### Diagnostics stop reporting false corruption (#168)

Clarion source is saved as Windows-1252/ANSI with no BOM, but four `File.ReadAllText` calls on the LSP text-sync path read it with no encoding argument &mdash; and .NET only auto-detects via BOM. Every single-byte high-bit character (a copyright symbol, say) silently became `U+FFFD` *before* the text reached the language server, which then correctly flagged the replacement character it had been handed. The result was waves of "this character will corrupt the file" warnings &mdash; dozens per file &mdash; on files that are perfectly valid on disk. All four sites now read through `EncodingHelper.DetectFileEncoding`, the same helper the diff viewer got in #94.

### Squiggles stop vanishing (#170)

The squiggle overlay could render nothing at all for a file that the diagnostics pill correctly reported an error for moments later. Four defects, one symptom, all rooted in treating "no information yet" as "authoritatively zero": a premature empty LSP republish was trusted as final (the server publishes progressively, and a slower cross-file check can land in a later batch); a timed-out round-trip was folded into an empty marker list, which *erased* every existing squiggle and its gutter mark rather than merely failing to add one; the client's timeout sat below the host's own worst case, discarding slow-but-successful analyses; and the settle loop parked a thread-pool thread per request. Empty results now get a short settle window before being believed, a timeout leaves the rendered markers alone, the diagnostics call gets its own longer budget while completion and hover keep their short interactive one, and the wait is properly async.

### Diagnostics window follows the CA Editor's theme &mdash; and appears at all (#169)

The LSP status bar and the diagnostics popup rendered in the chat pane's theme, which is a separate setting from the CA Editor's own. They now follow the **active editor's** theme, tracked per Monaco surface rather than read from a process-wide mirror that only ever recorded whichever page spoke last.

Three correctness bugs surfaced in the same code path and are fixed here too. The status bar pill **never appeared** when the IDE's own ClarionLsp addin was the active client &mdash; the visibility check asked the bundled `LspClient`, which in that configuration is never started, so the pill was hidden on every tick and the window it opens was unreachable. Both the liveness check and the cache read now go through `SharedLspBridge`, and the target file is resolved from the active editor instead of "the last file any LSP tool touched". The pill also stopped claiming a green **OK** for files nothing had ever been published about &mdash; unknown now renders as its own muted state rather than being flattened into "clean" during exactly the window when results are still arriving, and the status bar **asks** for diagnostics when it finds none cached instead of reporting "unknown" indefinitely at a cache nothing else was going to fill. And severity colours now survive a live dark&#8646;light switch instead of keeping the previous theme's palette until the rows next rebuilt.

Rounding it out: owner-drawn column headers and grid lines that actually follow the theme, a selection highlight that no longer overrides each row's severity colour, a dark-mode-aware native title bar, and no more hover flicker.

### Completion stops leaking other procedures' locals (#172)

Follow-up to #159. The CodeGraph backfill in bare-prefix completion matched symbol names across the whole indexed solution with no scope awareness, so a variable declared **private to some unrelated procedure in a different file** was offered exactly like a genuine global &mdash; typing `Include` at the top of a PROGRAM file could surface an `IncludeAddress` local from elsewhere entirely. Symbols the indexer already tags as procedure-private are now filtered out of that merge. Locals in the procedure you're actually standing in are unaffected: those come from a live-buffer parse, not the database.

### `DO` completes routines, and only routines

`DO` takes a ROUTINE label and nothing else, so it is now its own completion context answered from routines alone. Routine names are read **from the live buffer**, scoped to the enclosing procedure &mdash; which is a routine's real visibility in Clarion &mdash; so a routine you just typed and haven't saved completes too. Previously `DO` was answered from the general symbol set: typing `DO ref` offered methods from an unrelated `Reflection` class while missing the `RefreshWindow` routine a few lines up.

### Ctrl+X in the CA Editor reaches the clipboard (#173)

Clarion-style Ctrl+X posted the cut text to the host before deleting it from the buffer &mdash; and the CA Editor never implemented its half of that contract, so **Ctrl+X deleted the line without putting anything on the Windows clipboard**. Both stubs left inert since the original overlay spike are now wired: the cut text reaches `Clipboard.SetText`, and a Data-pad field dropped directly onto the editor surface now returns activation to the editor's own tab instead of leaving focus stranded on the pad.

### Show the diagnostics bar again after dismissing it

The LSP status bar &mdash; the strip at the bottom of the assistant pane carrying the diagnostics pill &mdash; has always had its own **&#10005;**, and nothing brought it back: restarting Clarion was the only way. A **&#9678;** button joins the header's title-row actions, beside the theme toggle, and shows or hides it on demand. It repaints from the current state on the way back rather than returning with whatever it was showing when dismissed.

<!-- release-docs: covered=deploy -->
### Clarion 10 builds again

`DiffService` called a `FileService` method that doesn't exist on Clarion 10's older SharpDevelop fork, so the C10 build had been failing outright since the CA Compare write-back work landed &mdash; while 11, 11.1 and 12 compiled clean. It now reaches the same information through an API present on every fork, from one code path.

**If you run Clarion 10, this release is the first to include roughly a week of changes** that never made it into a working C10 binary. The installer ships a per-Clarion build (`bin\Debug-C10` and siblings), so a broken build for one release meant that release shipping stale or not at all.

The deploy script no longer lets one bad target take the others down with it, either: a build failure for a single Clarion version used to abort the run *before* the deploy step, so **nothing** was deployed anywhere while the console showed the other three building successfully. Failures are now collected, every version that built is deployed, and the run ends by naming what didn't ship.

<!-- release-docs: covered=create-class -->
### Class model preview renders again (#171)

In **Create New Class**, any model whose declaration put a Clarion keyword and a quoted string on the same line &mdash; a standard `CLASS,TYPE,MODULE('X.CLW'),LINK('X.CLW')` &mdash; rendered visibly broken markup instead of coloured code. The keyword pass ran over the HTML the string pass had just produced and matched the literal `class` and `string` inside its own attributes. The two passes are now ordered so there is no HTML for the keyword pass to collide with.

### Smart formatter keeps comments where they belong (#161)

Two fixes to **Ctrl+I**, both reported and diagnosed by [@geircodes](https://github.com/geircodes).

A comment sitting among declarations &mdash; inside a `GROUP`/`QUEUE`/`RECORD`/`FILE`, or directly in a procedure's or routine's DATA section &mdash; was indented to the CODE-section column rather than the field column it had been aligned to. It visibly jumped left while every declaration around it formatted correctly, which read as arbitrary rather than as a rule; comments inside `IF`/`CASE`/`LOOP` bodies were never affected, which is what made it look inconsistent. Those comments now line up with the fields they sit among &mdash; and a long banner comment does *not* drag the whole structure's field column to the right with it.

**"Indent comments" now means what it says.** Switching it off used to *delete* a comment's indentation and dump it at column 1, including comments hand-aligned deep inside nested control structures. Off now means leave the comment exactly where it is.

### Thanks

- **geircodes** &mdash; the bulk of this cycle again: the LSP source-encoding fix that ended a wave of false "this character will corrupt the file" warnings (#168), the squiggle overlay going blank on slow or premature results (#170), the diagnostics window's theme plus three correctness bugs found alongside it &mdash; including the status bar pill that never appeared at all (#169), the completion scope leak that surfaced other procedures' locals solution-wide (#172), the CA Editor clipboard and drop-focus stubs (#173), and the class-model preview highlighting (#171). Also reported, diagnosed and wrote the patch for the Ctrl+I comment-indenting fixes (#161), filing it as an issue with the semantics question open rather than as a PR &mdash; which is why "Indent comments OFF" now means something deliberate.
- **Bill Atchison** &mdash; reporting that PDFs would not import (#167). The bug was invisible to anyone whose machine happened to carry a stray `pdftotext.exe`, which is every developer machine here; without the report it would have kept shipping.
- **BoxSoft** &mdash; the installer path report (#142) that turned out to be the reason a whole diagnostic round was spent chasing symptoms in a build that had already been replaced.

---

## Release History

Summaries for **v5.5 and earlier** &mdash; back to v3.0 &mdash; are archived in **[docs/releases/CHANGELOG.md](docs/releases/CHANGELOG.md)**.

Full per-release notes live in **[docs/releases/](docs/releases/)**.

---

## Also Included: COM for Clarion

The installer bundles **COM for Clarion**, a complete toolkit for creating .NET COM controls that work with Clarion:

- **IDE addin** &mdash; browse, discover, and manage COM controls from inside Clarion
- **UltimateCOM template** &mdash; Clarion template and class for embedding COM controls in your apps
- **ClarionCOM tooling** &mdash; project templates, build scripts, and deployment tools for creating your own C# COM controls
- **COM Marketplace** &mdash; access community-published controls from [clarionlive.com](https://clarionlive.com)

---

## Installation

### Prerequisites

| Requirement | Notes |
|---|---|
| **Clarion IDE** (v10, v11, or v12) | Auto-detected from Windows registry |
| **Claude Code CLI** | [Download from Anthropic](https://claude.ai/download) |
| **WebView2 Runtime** | Pre-installed on Windows 11; [download for Windows 10](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) |

### What leaves your machine

Clarion Assistant is a front end for the **Claude Code CLI**, and that CLI talks to Anthropic's API
over the internet. When you use the assistant, what you type and the file contents it reads on your
behalf &mdash; source, embed code, dictionary and schema details, build output &mdash; are sent to
Anthropic to produce a reply. The IDE tools described in this README are how the assistant reads
that context, so anything you point it at is potentially part of a request.

That is the product working as intended rather than a hidden behaviour, but it is your code, so it
should be stated plainly: **do not use it on material you are not permitted to send to a third-party
API.** Anthropic's terms and privacy policy govern what happens to it &mdash; see
[Anthropic Privacy](https://www.anthropic.com/legal/privacy). Your Claude Code account and its
settings, not this addin, control that relationship.

A few other features reach the network only when you explicitly invoke them: ingesting
documentation from a URL, GitHub operations, and the marketplace browser.

### Install

1. **[Download the latest installer](https://github.com/ClarionLive/ClarionAssistant/releases/latest)** (code-signed)
2. Close the Clarion IDE
3. Run the installer &mdash; select which Clarion versions to install for
4. Restart the Clarion IDE

**One row per Clarion version, and they are not interchangeable.** Each row installs the addin *built for that version*, compiled against that Clarion's own IDE assemblies &mdash; so pointing the "Clarion 10 folder" row at a Clarion 12 installation ships the wrong build and it won't load. The installer now checks the version of whatever folder you enter and warns you if it doesn't match the row.

**More than one installation of the same version?** That's supported &mdash; press the **`+`** button on that version's row and pick the extra folder. Each extra gets a copy of that row's addin once the install finishes, and the list is remembered for next time. Handy if you keep, say, two Clarion 12 trees side by side.

> One trap worth knowing: never leave a spare or backup copy of the addin folder anywhere *inside* an `accessory\addins` tree. Clarion scans subfolders, and a duplicate makes startup fail with *"Identity name used by multiple addins."* Keep backups outside.

### What Gets Installed

| Component | Location | Description |
|---|---|---|
| Clarion Assistant addin | `{Clarion}\accessory\addins\ClarionAssistant\` | Main addin DLL, WebView2, SQLite, HTML terminal |
| COM for Clarion addin | `{Clarion}\accessory\addins\ComForClarion\` | COM browser addin |
| UltimateCOM template | `{Clarion}\accessory\template\win\` | .tpl, .inc, .clw, and template DLLs |
| Documentation | `{Clarion}\accessory\resources\ComForClarionDocumentation\` | COM for Clarion docs |
| Claude Code plugin | `%USERPROFILE%\.claude\plugins\...\clarion-assistant\` | 20+ Clarion-specific skills, hooks, and docs |
| Code quality agents | `%USERPROFILE%\.claude\agents\` | 6 agents (won't overwrite existing) |
| ClarionCOM tooling | `%APPDATA%\ClarionCOM\` | Project templates and scripts |
| DocGraph database | `%APPDATA%\ClarionAssistant\` | Pre-loaded Clarion 12 documentation index |

Your existing Claude Code settings are preserved &mdash; the installer merges permissions non-destructively.

---

## MCP Tools Reference

Clarion Assistant exposes **108 MCP tools** that Claude uses to interact with the IDE:

### IDE & Editor (23 tools)
| Tool | Description |
|---|---|
| `get_active_file` | Get path and content of the open file |
| `open_file` | Open a file in the editor, optionally at a line |
| `close_file` | Close the active editor tab |
| `save_file` | Save the active file |
| `get_open_files` | List all open editor tabs |
| `go_to_line` | Navigate to a specific line in the open file |
| `get_cursor_position` | Get current line, column, and total line count |
| `get_line_text` | Get text of a specific line from the live buffer |
| `get_lines_range` | Get a range of lines from the editor |
| `get_selected_text` | Get the currently selected text |
| `get_word_under_cursor` | Get the word at the cursor position |
| `select_range` | Select/highlight a range of text in the editor |
| `insert_text_at_cursor` | Insert text at the current cursor position |
| `replace_text` | Find and replace all occurrences in the active editor |
| `replace_range` | Replace text between specific line/column positions |
| `delete_range` | Delete text between specific line/column positions |
| `find_in_file` | Search for text in the active editor buffer |
| `toggle_comment` | Toggle Clarion line comments on a range of lines |
| `is_modified` | Check if the active file has unsaved changes |
| `undo` | Undo the last edit |
| `redo` | Redo the last undone edit |
| `show_diff` | Show a side-by-side diff in the Monaco viewer |
| `get_diff_result` | Get approval/notes from the diff viewer |

### Application Tree & Embeditor (21 tools)
| Tool | Description |
|---|---|
| `get_app_info` | Get info about the currently open app |
| `list_procedures` | List all procedures in the open app |
| `get_procedure_details` | Get detailed procedure info (prototype, module, template) |
| `select_procedure` | Select a procedure in the app tree |
| `open_procedure_embed` | Open the embeditor for a procedure |
| `get_embed_info` | Get info about the active embeditor |
| `list_embeds` | List all embed sections with filled status |
| `find_embed` | Find and navigate to an embed section by name |
| `next_embed` / `prev_embed` | Navigate to the next/previous embed point |
| `next_filled_embed` / `prev_filled_embed` | Navigate to the next/previous filled embed |
| `get_embed_content` | Read code inside a specific embed slot |
| `get_embeditor_source` | Get full annotated embeditor source with embed markers |
| `search_embeditor_source` | Regex search over annotated embeditor source |
| `open_embeditor_source` | Open the embeditor source in the editor |
| `write_embed_content` | Write code into an embed slot by line number |
| `save_and_close_embeditor` | Save changes and close the embeditor |
| `cancel_embeditor` | Discard changes and close the embeditor |
| `export_txa` | Export app or procedures to TXA format |
| `import_txa` | Import a TXA file into the app |

### Code Intelligence (11 tools)
| Tool | Description |
|---|---|
| `get_solution_info` | Get current solution, Clarion version, RED file, and CodeGraph status |
| `index_codegraph` | Index the solution for CodeGraph queries |
| `index_solution` | Index all projects in the solution |
| `list_codegraph_databases` | List available indexed CodeGraph databases |
| `query_codegraph` | SQL queries over every symbol, relationship, and call chain |
| `get_project_source_files` | List all source files (.clw, .inc) with absolute paths |
| `analyze_class` | Parse CLASS definitions from .inc files |
| `sync_check` | Compare .inc declarations vs .clw implementations |
| `generate_stubs` | Generate method stubs for missing implementations |
| `generate_clw` | Generate a complete .clw implementation from a .inc file |
| `generate_source` | Generate .clw/.inc source from templates |

### LSP &mdash; Language Server (9 tools)
| Tool | Description |
|---|---|
| `lsp_start` | Start the Clarion Language Server |
| `lsp_debug_status` | Check LSP server status |
| `lsp_definition` | Go to definition of a symbol (cross-file) |
| `lsp_references` | Find all references to a symbol across the workspace |
| `lsp_hover` | Get type info, signature, and documentation for a symbol |
| `lsp_document_symbols` | Get all symbols in a file |
| `lsp_find_symbol` | Search for symbols across the workspace by name |
| `lsp_diagnostics` | Get errors and warnings for a source file |
| `lsp_rename` | Propose a rename of a symbol (returns edit list for approval) |

### Schema Intelligence (10 tools)
| Tool | Description |
|---|---|
| `search_tables` | Search database tables by name |
| `get_table` | Full table detail with columns, keys, relationships |
| `search_columns` | Find columns across all tables |
| `get_relationships` | Show parent/child table relationships |
| `query_schema` | Run SQL queries against the schema index |
| `schema_stats` | Get schema database statistics |
| `ingest_schema` | Index a Clarion dictionary (.dctx) |
| `ingest_sql_database` | Index schema from SQL Server, SQLite, or PostgreSQL |
| `export_dctx` | Export dictionary to .dctx format |
| `import_dctx` | Import a .dctx dictionary |

### Documentation Search (6 tools)
| Tool | Description |
|---|---|
| `query_docs` | Full-text search across all indexed documentation |
| `ingest_docs` | Index docs from a Clarion installation's accessory/Documents folder |
| `ingest_web_docs` | Ingest documentation from web URLs |
| `list_doc_libraries` | List all indexed libraries with chunk counts |
| `discover_docs` | Preview discoverable doc sources without ingesting |
| `docgraph_stats` | Get DocGraph database statistics |

### Build Tools (5 tools)
| Tool | Description |
|---|---|
| `build_solution` | Build the entire Clarion solution via ClarionCL.exe |
| `build_app` | Build a single .app file (for multi-DLL solutions) |
| `build_com_project` | Build a C# COM control via MSBuild |
| `run_command` | Execute any command-line tool |
| `execute_command` | Execute a shell command |

### File System & Search (7 tools)
| Tool | Description |
|---|---|
| `read_file` | Read file content from disk with optional line range |
| `write_file` | Write content to a file |
| `append_to_file` | Append text to an existing file |
| `list_directory` | List files in a directory with optional pattern filter |
| `search_files` | Search for files by name |
| `search_files_advanced` | Advanced file search with Everything integration (path, extension, size, date filters) |
| `search_content` | Search file contents by text |

### Project & IDE (4 tools)
| Tool | Description |
|---|---|
| `get_ca_project_info` | Get linked GitHub/Bitbucket account and repo for a project |
| `get_red_search_paths` | Get RED file search paths for the active solution |
| `resolve_red_path` | Resolve a filename to an absolute path via RED search paths |
| `inspect_ide` | Inspect Clarion IDE internal state |

### Knowledge & Memory (6 tools)
| Tool | Description |
|---|---|
| `add_knowledge` | Save reusable insights (decisions, patterns, gotchas) across sessions |
| `query_knowledge` | Search past decisions and patterns |
| `save_session_summary` | Save a session summary for next-session continuity |
| `query_traces` | Query code generation traces |
| `trace_stats` | Get trace database statistics |
| `log_skill_update` | Log a skill update event |

### Multi-Instance Coordination (4 tools)
| Tool | Description |
|---|---|
| `list_instances` | List all running Clarion Assistant instances |
| `get_instance_messages` | Get messages from other instances |
| `send_to_instances` | Send a message to other instances |
| `check_conflicts` | Check for file conflicts across instances |

### Validation (2 tools)
| Tool | Description |
|---|---|
| `validate_names` | Validate Clarion naming conventions |
| `find_duplicates` | Find duplicate symbols in the solution |

---

## Claude Code Skills

The installer includes 22 Clarion-specific skills for Claude Code (installed as a plugin):

| Skill | Description |
|---|---|
| `clarion` | Clarion language reference &mdash; syntax, data types, control structures, Windows API patterns |
| `clarion-ide-addin` | IDE addin development with SharpDevelop integration |
| `clarion-analyze` | Analyze Clarion code generation traces for recurring failure patterns |
| `clarion-benchmark` | Benchmark Clarion code generation quality |
| `clarion-convert-driver` | Convert Clarion dictionaries between file drivers (e.g., TopSpeed to SQLite) |
| `evaluate-code` | Evaluate Clarion app code for issues and improvements |
| `jfiles` | jFiles JSON serialization patterns for Clarion |
| `lsp-diagnostics` | Run LSP diagnostics across all source files in the open solution with navigate-to-error support |
| `ClarionCOM` | Interactive COM development assistant |
| `clarioncom-build` | Build COM projects with MSBuild |
| `clarioncom-config` | Manage ClarionCOM settings |
| `clarioncom-control` | Create and validate C# COM controls for Clarion |
| `clarioncom-create` | Create new C# COM control projects from scratch |
| `clarioncom-deploy` | Generate deployment artifacts |
| `clarioncom-get` | Download controls from the marketplace |
| `clarioncom-github-init` | Initialize GitHub repos for COM projects |
| `clarioncom-marketplace-submit` | Submit controls to the COM Marketplace |
| `clarioncom-validate` | Validate RegFree COM compliance |
| `clarioncom-webview2-build` | Build WebView2 COM control projects |
| `clarioncom-webview2-create` | Create WebView2-based COM controls with HTML/CSS/JS |
| `clarioncom-webview2-deploy` | Generate deployment artifacts for WebView2 COM controls |
| `clarioncom-webview2-validate` | Validate WebView2 COM controls for RegFree compliance |

---

## Building from Source

### Requirements

- Visual Studio 2022 (Community or higher)
- .NET Framework 4.8 SDK
- Clarion IDE (for reference assemblies in `{Clarion}\bin\`)
- [Inno Setup 6](https://jrsoftware.org/isdownload.php) (for building the installer)

### Configuring your Clarion path

The build uses `Directory.Build.props` at the repo root to locate your Clarion installation. The defaults assume John's machine layout (`C:\Clarion12`, `C:\Clarion11-13372`, `C:\Clarion10`).

If your Clarion is installed elsewhere, create a `Directory.Build.props.user` file alongside `Directory.Build.props` (it is gitignored — never commit it):

```xml
<Project>
  <!-- Replace with your actual Clarion installation path -->
  <PropertyGroup>
    <ClarionRoot>C:\Clarion\Clarion12</ClarionRoot>
  </PropertyGroup>
</Project>
```

The `.user` file overrides the defaults for all `ClarionVersion` values, so a single path entry is enough if you only build for one version. You can still pass `/p:ClarionVersion=11` on the command line to select the target version.

Alternatively, pass the path directly on the command line without creating a `.user` file:

```powershell
msbuild ClarionAssistant.csproj /p:ClarionVersion=11 /p:ClarionRoot="C:\Clarion\Clarion11.1"
```

### Build

```powershell
# Build for a specific version (uses Directory.Build.props.user if present)
cd ClarionAssistant
msbuild ClarionAssistant.csproj /p:Configuration=Debug /p:ClarionVersion=12

# Build the addin for all Clarion versions via deploy script
.\deploy.ps1 -NoBuild:$false -Version all
```

> **Note:** Use MSBuild directly — do **not** use `dotnet build`. WebView2 NuGet resolution fails with the .NET CLI on this .NET Framework 4.8 project.

### Deploy for Development

```powershell
# Deploy to your local Clarion IDE (builds + copies DLLs)
cd ClarionAssistant
.\deploy.ps1 -Version 12

# Deploy without rebuilding (e.g. HTML-only changes)
.\deploy.ps1 -Version 12 -NoBuild

# Kill the IDE before deploying (when DLLs are locked)
.\deploy.ps1 -Version 12 -Kill
```

### Running the tests

```powershell
cd ClarionAssistant
.\tests\Run-Tests.ps1
```

One entry point for both harness families: standalone `csc` harnesses over IDE-free service code
(`tests\`), and node harnesses over the Monaco WebView2 pages (`Terminal\test\`). Neither is wired into
MSBuild &mdash; they exist to be run before you deploy, because the bugs they catch (a NUL byte inside a
420 KB HTML file, a settings panel that reads fine in dark mode and is illegible in light) pass a clean
build and fail a human.

Most need nothing installed. One page test needs `jsdom`, declared as a devDependency:

```powershell
npm install --prefix Terminal\test
```

Without it that test reports *could not run* and fails the overall run rather than reporting green. See
[`tests/README.md`](ClarionAssistant/tests/README.md) for what each harness guards.

---

## Acknowledgments

Clarion Assistant is built with the help of these open-source projects and contributors:

### Contributors

| Name | Contribution |
|---|---|
| [Mark Sarson](https://github.com/msarson/Clarion-Extension) | Clarion Language Server Protocol implementation for VS Code, which the LSP integration in Clarion Assistant is based on |

### Open Source Libraries

| Library | Description | License |
|---|---|---|
| [xterm.js](https://github.com/xtermjs/xterm.js) | Terminal emulator (v6.0.0) | MIT |
| [Newtonsoft.Json](https://github.com/JamesNK/Newtonsoft.Json) | JSON serialization (v13.0.3) | MIT |
| [System.Data.SQLite](https://system.data.sqlite.org) | SQLite database with FTS5 full-text search | Public Domain |
| [Microsoft WebView2](https://github.com/MicrosoftEdge/WebView2Feedback) | Embedded browser runtime | MIT |
| [Everything SDK](https://www.voidtools.com) | Instant file search by voidtools | Freeware |
| [recursive-improve](https://github.com/kayba-ai/recursive-improve) | Recursive improvement pattern for code generation | MIT |

---

## License

[MIT License](LICENSE) &mdash; &copy; 2025-2026 ClarionLive.

The MIT license covers Clarion Assistant's own source. The installer additionally bundles third-party components that keep their own licenses &mdash; PdfPig (Apache-2.0), the Microsoft Edge WebView2 runtime, SQLite, and Node.js with the bundled language server. Clarion IDE assemblies are referenced from your existing Clarion installation and are not redistributed.
