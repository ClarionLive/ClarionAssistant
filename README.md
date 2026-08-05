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
- **Code Snippets (Ctrl+J)** &mdash; classic Clarion template-picker parity: insert reusable code with tab-stops and a `${SELECTED}` placeholder, managed from Settings &rarr; Snippets
- **CA Explorer** &mdash; docked pad showing the active CA Embeditor tab's Local, Module &amp; Global Data, Declared Tables, Other Files, and their Keys, Columns, and Relations; drag a field to the editor or Window designer, copy/paste variables native-style, and a Cheat Sheet tab of editor shortcuts
- **Evaluate Code** &mdash; interactive code review for entire apps, procedures, open files, or selected code
- **CA Find & Replace** &mdash; dockable Find pad or classic in-editor overlay (your pick), Find-All with results in their own editor tab, and one shared history across every CA surface
- **Document Structure** &mdash; fly-out outline of the current buffer with symbol icons, Class &#9656; Methods regrouping, and filtering; click to navigate
- **Diff viewer** &mdash; Monaco-based side-by-side diffs with syntax highlighting, live-buffer diffing, and a current-line comparison panel
- **Knowledge system** &mdash; persistent cross-session memory for decisions, patterns, and gotchas
- **Zoom persistence** &mdash; Ctrl+mousewheel zoom is saved and restored across sessions

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

Verified against a fixed set of eight retrieval tests, kept with the code at `docs/DocGraph-Chunking-Verification.md`, including a guard on the date-picture table above so a future chunking change cannot quietly undo the extraction fix.

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

## What's New in v5.5

v5.5 turns two read-only surfaces into working ones &mdash; editable diffs and a drag-and-drop Data pad &mdash; and fixes a CodeGraph misattribution that was hiding 59% of the call graph. Full notes: [docs/releases/v5.5.0.md](docs/releases/v5.5.0.md).

### CA Compare &mdash; editable diffs

The diff viewer becomes a working surface rather than a read-only one. Both panes unlock for editing, and each hunk gets a single **apply-change arrow** (Monaco's own gutter revert control) that copies it to the other side; copy direction is chosen per-compare, not per-hunk. Each side saves independently through a write-back path that can't clobber an edit made on the other, **Ctrl+S** saves every changed side at once, and closing the tab &mdash; not just the Close button &mdash; prompts when either side is dirty. The file-path bar shows full paths.

### CA Explorer &mdash; Files tab restructure

The omnibox is rebuilt around a split-button **Open** plus a single search field, joined by a type dropdown, scope pills, and a header **Trace** with mouse-pickable targets, sticky headers, and an actionable trace log. **Compare pairs are remembered** &mdash; re-run, swap, or pin a pair straight from the Files tab. The environment banner names the active `.red`, and view state round-trips so the tab comes back the way you left it.

### Data pad &mdash; master/detail and field drag-and-drop

Variable rows and the table tree both gain master/detail: type-icon slide-down panels for variables, and table/key/column/relation panels for the tree, with an icon-only header toolbar. Column detail now comes from the **live dictionary** in full, including auto-number and exclude chips on keys. Fields can be **dragged from the pad into an editor** &mdash; Monaco surfaces and legacy ICSharpCode editors both accept a point-drop, with visible drop feedback while you drag.

### Zero-reload error navigation

Clicking a row in the IDE's Errors pane used to close and reopen the embeditor. Navigation is now intercepted and dispatched into the **live overlay**, repositioning it in place &mdash; and unsaved overlay edits survive Clarion's own embed re-open via a mirror/stash/restore path.

### Horizontal split panes (#157)

The editor **Split** button gains an orientation choice: right-click it for **Vertical split (side by side)** or **Horizontal split (top / bottom)**. Choosing an orientation while already split repositions the panes in place &mdash; content, scroll position, and cursor are all preserved; choosing one while unsplit opens the split immediately in that orientation. The choice persists across sessions. The divider now tracks the true midpoint of the editing area, so toggling the Outline or Find-All column while split no longer opens a gap.

### Sort the Document Structure outline (#153)

A new sort toggle in the outline toolbar orders symbols **A&ndash;Z independently at each level** &mdash; a class's methods reorder among themselves while the hierarchy stays intact and siblings never migrate between levels. Toggle it off for document order. Works alongside the existing filter.

### Split a selection into multiple snippet parameters (#154)

Code Snippets (Ctrl+J) gain `${SELECTED:N}` &mdash; the Nth comma-separated part of the current selection (1-based, trimmed), alongside the existing `${SELECTED}` (whole selection). Select `Clientes,CLI:CLI01` and a snippet body like `Access:${SELECTED:1}.Fetch(${SELECTED:2})` expands to `Access:Clientes.Fetch(CLI:CLI01)`. With nothing selected, each distinct `${SELECTED:N}` becomes its own fillable tab-stop instead of a blank hole &mdash; same as `${SELECTED}` already did. See the examples panel (the **?** next to the snippet Body field) for a worked example.

### Field-equate completion on `?` (#159/#160)

Typing `?` now triggers completion directly, and field equates come back as a Reference kind with their own icon. Bare-prefix globals are no longer merged into that list &mdash; previously a variable like `LDField` could prefix-match and outrank the `?LdapButton` you were actually reaching for.

### Contract all / Expand all (#147)

A fold button joins the embeditor toolbar. Click contracts every structure, click again expands, and the icon shows which the next click will do; right-click offers both explicitly. The state is read from the live folding model each time rather than remembered, so folding a region by hand from the gutter can't leave the icon promising the wrong action. Expand is offered only once *everything* foldable is collapsed, so a single hand-folded region doesn't hijack the primary action.

### Code suggestions quote the code (#69)

The assistant now quotes the surrounding lines when it points you at a change, instead of citing a bare line number. This matters most in template-generated `.clw` files: they're regenerated, so a line number goes stale the moment the template runs again, while the surrounding code stays recognizable. A useful side effect &mdash; because locating a change now means reading the real generated source, the assistant can also tell you which **embed** the change belongs in.

### Native embeditor parity and editor options

Long-standing parity gaps close together. **Ctrl+Q** mimics the native embeditor's save-and-exit gesture (#137) &mdash; a dirty buffer raises a confirm where Enter saves and exits, absorbing the trailing Enter of the native muscle memory. The as-you-type aids are now **individually switchable** in the gear panel (#138): *Auto-space assignments (=)* and *Live keyword casing* can each be turned off. `OMIT('term')` and `COMPILE('term')` blocks fold (#133), shared across the embeditor, diff editor, and sticky scroll. Indentation follows **Options &rarr; Text Editor &rarr; Behavior** &mdash; Indentation size, Tab size, and Convert Tabs to Spaces &mdash; instead of assuming 4, via a "Follow Clarion's editor options" checkbox that's on by default (#126).

### CA Editor reliability

The editor watches its file on disk for external read-only and content changes (#146), saves dirty tabs **before a build starts** (#134), and the **Reload** button now actually works (#135) &mdash; it was a no-op since day one &mdash; restoring caret and scroll position when it does. A new `get_diff_content` MCP tool shares a common unified-diff generator with the diff viewer (#131), and the live Monaco cursor is exposed over a cross-addin reflection contract (#149) for consumers like the CA Debugger.

### CodeGraph &mdash; overloaded and shadowed names attribute to the right procedure (#165)

"What does this procedure call?" collapsed every same-named procedure onto one arbitrary winner. `ResolveRelationships` tracked which procedure a line sat inside using dictionaries keyed by **name only**, which can hold one id per name &mdash; so all of these folded together: genuine parameter-type overloads (`Dispatch(LONG)` vs `Dispatch(*CSTRING)`), case variants (`Setup` alongside `SetUp`), and a local **ROUTINE sharing its enclosing PROCEDURE's name**. Every call and reference from any of them was credited to whichever loaded last. In one production module, an entire procedure body was attributed to a routine defined 50 lines *below* the code in question. Resolution is now keyed on each procedure's own definition line, which is unique per file, and the current procedure's name is tracked alongside its id instead of being recovered by reverse-scanning a name-keyed dictionary.

Measured across a 1,659-file production solution: **no call sites lost**, 19,774 gained (`calls` rows up 59%, from 33,319 to 53,093), and **6,240 procedures that never appeared as a caller at all** now do. Symbol counts are byte-identical before and after &mdash; symbol capture was never the broken part. Removing four O(symbols) reverse-scans from the per-line loop also cut full-index time on that solution by roughly **40%** (29.5 min &rarr; 17.6 min).

> **Re-index your solutions after upgrading.** This changes what the indexer *writes*, so every existing `.codegraph.db` still holds the old attribution and will keep returning the pre-fix answers until it's rebuilt. Use **Reindex** (full rebuild) &mdash; **Update** is not enough: the incremental path compares source timestamps, and since your code hasn't changed it reports "0 of N projects have changes" and skips the work. Same story for the MCP `index_codegraph` tool: pass a full index, not incremental. Library graphs under `%APPDATA%\ClarionAssistant\clariongraph\` want the same treatment via **Build Library**.

### Hover placement, size, and clickable locations (#164)

Hovers render **below** the hovered line instead of above it, nudged clear of the label column &mdash; Monaco's default put the tooltip squarely on top of the lines you were just reading, which in Clarion is the column-0 label area. The widget also renders one pixel under the editor font rather than inheriting it outright, tracking both the gear panel's font size and Ctrl+wheel zoom. Location footers in a hover (`Foo.inc:12 → Foo.clw:340`) become clickable and navigate the IDE, replacing a default handler that treated them as external links. The click path is live now but stays quiet until the companion language-server change ships &mdash; nothing emits those links yet.

### Fixed

- **Hover inside a string or comment showed an unrelated symbol** (#166) &mdash; hovering the `'Total'` argument in `SomeClass.SetValue('Total', 2, ...)` surfaced the info for a local variable named `Total` declared elsewhere in the procedure. The language server was right to return nothing; that empty answer is exactly what triggered CodeGraph's hover fallback, which matched identifier-shaped text with a plain regex and no idea it sat inside quotes. The fallback now bails on strings and `!` comments the same way the server already did &mdash; `''` treated as an escaped quote, both delimiting quotes counted as inside.
- **Output pane deafness** (#140) &mdash; while a Monaco editor was the active document, the IDE's Output pane (Build / Generator / Debug) couldn't be clicked into, drag-selected, or Ctrl+A'd; only scroll worked. Monaco no longer steals focus from IDE pads, with no regression to editor-focus-on-tab-switch.
- **Hover and completion respect procedure scope** (#152) &mdash; a local, routine, or module variable declared in the current buffer now outranks a same-named symbol anywhere else in the solution or library, and a not-yet-indexed local procedure resolves from the buffer. A column-1 declaration in progress no longer leaks the *previous* procedure's locals into the completion list, and an exact-match identifier outranks a keyword that merely prefixes it (typing `PRO` where a local `PRO` exists no longer commits `PROCEDURE`).
- **Phantom "END has no matching structure" warnings** (#156) &mdash; bare screen `GROUP` controls in a WINDOW and anonymous `RECORD, PRE()` structures inside a FILE no longer desync the embed slot's structure-balance checker, while a plain variable named `Group` or `Record` still correctly produces no warning. Shipped with a repro fixture that must yield zero diagnostics.
- **Cold-open navigation lands on the right line** (#162) &mdash; Errors-pane clicks, bookmarks, and Find Next targeting a file that isn't open yet could silently drop the requested line and open wherever the file was last positioned. The requested position is now parked before the file opens, closing the race for every caller that drives the hidden caret directly.
- **Duplicate entries in member completion** (#151) &mdash; dedupe now keys on each item's bare identifier rather than its display label, so a member the language server already resolved can't be re-added under its bare name.
- **More keyword-as-label misreads** (#132/#136/#150) &mdash; `MENU`, `MENUBAR`, `SHEET`, `TAB`, `OPTION`, and `TOOLBAR` used as plain variable names no longer read as structure openers, and the label check is now strict column 0, matching the real compiler rule.
- **Cross-instance WebView2 crashes** (#141) &mdash; each Clarion process gets an isolated WebView2 profile, allocated from stable slots rather than per-PID folders.
- **Errors-pane caret mirroring** (#144) &mdash; native caret jumps are mirrored into the Monaco overlay, so an error click lands where it should.
- **Unsaved edits preserved** &mdash; overlay edits survive Clarion's embed re-open, and a disk-watched file turning read-only no longer discards them.
- **Go-to-definition no longer jumps to itself** (#143) &mdash; the fallback path could resolve a symbol to the very line you invoked it from.
- **Dead overlay Find-All results-tools row removed** (#116/#145) &mdash; Copy / Clear were wired but could never render.
- **Fold triangles on ordinary statement lines** (#158) &mdash; the folding scanner matched structure keywords inside string literals, so `MESSAGE('Unable to initialize Session Class instance')` or a `'Content-Type: application/xml'` header opened a phantom fold that swallowed the enclosing structure. One production 9,000-line file contained 64 such literals. String contents are now blanked before the keyword scan &mdash; which also makes comment-stripping literal-aware, so a `!` inside a string no longer truncates the line. Fixes the CA Embeditor, both diff panes and sticky-scroll headers at once.
- **CA Find opened dark regardless of your theme** (#148) &mdash; the pad only restored a theme it had saved itself, falling back to dark otherwise. It now seeds from Clarion Assistant's own light/dark setting, while a preference set inside the pad still wins.
- **CA Find** no longer misses keyboard focus on the first Ctrl+F after IDE start (#139).
- **CA Explorer polish** &mdash; dark-mode secondary text lifted above the WCAG AA contrast floor; dropdown menus kept inside the WebView2; tooltips flipped above the native drop strip; the last row's path line no longer clipped; the Files tab stops jumping on every activation.

### Known issues

- **Check the install folder if you have two installs of the same Clarion version** (#142). The installer derives each Clarion root from the registry (`HKLM\SOFTWARE\SoftVelocity\Clarion<version>\root`) on every run, and does **not** remember a folder you picked by hand last time. If the registry points at one Clarion 11 install and you actually launch a different one, setup will default to the registry's copy and quietly install where you don't run it &mdash; and the symptom is confusing, because the IDE then loads an older addin and behaves like the bugs you just upgraded to fix. Verify the path on the install page, and if you correct it, expect to correct it again next time. If you're unsure which build you're actually running, the CA panel reports the version of the DLL Clarion loaded &mdash; if that doesn't match the release you installed, this is why. A proper fix (remembering your choice) is queued for the next cycle.

### Thanks

- **geircodes** &mdash; the bulk of this cycle: horizontal split panes (#157), outline sort (#153), field-equate completion (#159/#160), scoping fixes (#152), GROUP/RECORD and keyword-as-label diagnostics (#132, #136, #150, #156), cold-open navigation (#162), completion dedupe (#151), CA Editor reliability (#134, #135, #146), WebView2 profile isolation (#141), CA Find focus (#139), Errors-pane caret mirroring (#144), go-to-definition self-jump (#143), `get_diff_content` (#131), and the cross-addin cursor contract (#149).
- **Mark Sarson** &mdash; removing the dead overlay Find-All results-tools row (#116/#145).
- **Andrew Santarelli** &mdash; snippet parameter splitting (#155).
- **BoxSoft** &mdash; Ctrl+Q parity (#137), the as-you-type opt-outs (#138), and the case for quoting code over line numbers (#69).
- **armisoftware** &mdash; OMIT/COMPILE folding (#133), Contract all / Expand all (#147), and the Find pad's theme (#148).
- **geircodes** also root-caused the phantom fold triangles (#158), including the measurement &mdash; 64 colliding string literals in a single 9,000-line file &mdash; that showed it was systemic rather than a two-line curiosity; and closed out the cycle with the CodeGraph overload-attribution fix (#165), the string/comment hover guard (#166), and the hover placement work (#164). The overload fix shipped with a compiling repro fixture and its own regression SQL, and quantified the damage first: 4,372 relationship rows collapsed onto one overload per name, with 79 of 145 overload symbols completely invisible as callers.

---

## What's New in v5.4

v5.4 is the biggest community cycle yet. Full notes: [docs/releases/v5.4.0.md](docs/releases/v5.4.0.md).

### CA Find & Replace suite (#66)
Find & Replace becomes a first-class subsystem: a dockable **CA Find pad** (the default for Ctrl+F/Ctrl+H), an optional classic **in-editor overlay** (Options &rarr; Clarion Assistant &rarr; Find / Replace), a **Find-All** column whose results can open in their own editor tab with context, click-through, and filtering (#113/#114), and **one shared find/replace history** across every CA surface with a per-procedure recent group.

### Document Structure outline
A fly-out outline of the current buffer &mdash; procedures, routines, classes and data with VS Code's symbol icons, Class &#9656; Methods regrouping, filtering, and expand/collapse-all. Click to jump; Navigate Back returns you.

### Parameter hints, Ctrl+F12, real-scope embeds
Signature help while typing a call (#75) and go-to-implementation (#78) in both Monaco surfaces. The embed buffer is now presented to the language server as a real MEMBER module of your PROGRAM, so completion/hover/F12 inside embeds see your global data (#74). `:` triggers completion like `.` (#52).

### Dictionary- and CodeGraph-aware completion in the CA Embeditor
Completion reaches into your ingested **SchemaGraph** (dictionary tables/fields/keys) and **CodeGraph** (solution-wide symbols) databases (#99): `PRE:` prefixes suggest that table's columns and keys with declared types and descriptions; bare prefixes suggest dictionary table names; variable completions show real declared types and scope. Requires a SchemaGraph source indexed once via Solution Settings &rarr; Schema Sources.

### Navigation history that keeps its promises
Back/Forward reliably return to the position the editor opened at, outline jump targets, go-to-definition origins, and host-driven jumps &mdash; explicit targets are pinned so a follow-up click can't erase them.

### Dark mode & accessibility
No white flash opening the embeditor in dark mode; contrast-guarded toolbar text on dark IDE chrome; Monaco's High Contrast toggle persists across sessions with visibly shaded read-only sections; CA Explorer tooltips drawn in-page (fixes a stuck-tooltip bug, #108).

### CodeGraph accuracy campaign
~20 indexer fixes driven by real production code (#79&ndash;#93, #97, #112, #118): INCLUDE discovery, typed PROCEDURE parameters, cross-file and inherited member call resolution, every GROUP/QUEUE/CLASS declaration spelling, built-in name collisions &mdash; with the repro solution vendored as a regression fixture.

### Clarion 11.1, VS2026, portable installs (#91)
Clarion **11.1** is a distinct build/deploy/install target; the build supports VS2026 Build Tools; installer paths are portable.

### Diff, terminal & reliability
Live-buffer diffs with encoding detection (#94), a current-line comparison panel (#96), clipboard-image paste as @-reference (#95); embeditor tab-switch no longer reloads/loses edits (#76), WebView2 crash process reaping (#109/#120), bundled LSP re-pinned to Clarion-Extension v1.0.0 (#77 &mdash; the per-configuration redirection fix), server re-root on solution switch (#106).

### Thanks
Community contributions from **Mark Sarson**, **geircodes**, **Andrew Santarelli**, **OkayPlunk**, plus reports from **BoxSoft** and **Bill Atchison**.

---

## What's New in v5.3

v5.3 is a refinement release &mdash; it sharpens the Monaco editor, Smart Formatter, and completion that landed in v5.1/v5.2, adds editor personalization and one-click surface toggles, and folds in a round of community fixes.

### Editor & Embeditor toggles on the IDE toolbar

Two new buttons on the main Clarion IDE toolbar let you flip the **CA Editor** and **CA Embeditor** overlays on or off without opening Settings &mdash; handy when you want to drop back to the native editor for a moment.

### Editor personalization

The gear panel gains two long-requested settings:

- **Font family** &mdash; pick the typeface for the Monaco source editor and CA Embeditor.
- **Occurrence highlighting** &mdash; toggle the highlight of other instances of the symbol under the cursor.

### Richer completion

Bare-prefix completion now also surfaces **module-level data** (variables declared outside any procedure) and **module-local procedures** (declared in the module MAP or as in-buffer sibling implementations), each with type/signature detail &mdash; on top of the globals, classes, and procedure locals added in v5.1.

### Smart Formatter & editor fixes

- **Built-in functions in call position** are cased correctly by **Ctrl+I** and as you type.
- **Keyword-named data labels** (e.g. a variable named like a statement) no longer open a phantom structure during formatting.
- **MAP formatting** and keyword-first completion behave correctly.
- **Case commands with no selection** now act on the character at the cursor.
- **Find matches track live buffer edits** &mdash; F3 and replace land at the correct positions after you've edited the buffer.
- **Commit-on-insert-key** works for keyword completions, with `{` added as a commit key.
- **CA Embeditor overlay** navigates to the native caret position when it attaches, so you start where you left off.

### Reliability

- **MCP HTTP listener** no longer deadlocks when the IDE UI thread is busy.
- **Windows PowerShell 5.1** can parse the bundled scripts (UTF-8 BOM), and the indexer's SQLite reference is fixed.

### Thanks

Community contributions in this release from **Andrew Santarelli** (#71), **Mark Sarson** (#68), **bdinko** (#59), and **Aarhusdk** (#57).

---

## What's New in v5.2

v5.2 makes the CA Embeditor feel completely native: it now rides Clarion's **own** "Embeditor Source" command instead of a separate menu item, and it appears with no flicker.

### CA Embeditor rides Clarion's native "Embeditor Source"

There is no longer a separate **Open in CA Embeditor** popup item. Just open an embed the way you always have &mdash; right-click a procedure &rarr; **Embeditor Source**, or the **Embeditor** button on the Views toolbar &mdash; and the Monaco CA Embeditor overlays the native embeditor automatically. One embed at a time, exactly like the native editor; edits save straight back with **Save &amp; Exit** (no re-open).

- **Nothing new to learn** &mdash; the trigger is Clarion's existing command, so it works from every place that opens an embed.
- **Toggle** the overlay under **Options &rarr; Clarion Assistant &rarr; Editor Surfaces** (&ldquo;Use the Monaco embeditor&rdquo;, on by default).
- **Flicker-free** &mdash; the overlay is placed the instant the embed view opens, before the native editor paints, so you don't see the native embeditor flash first.

*(Thanks to Mark Sarson &mdash; this adopts the auto-detect approach from his [ClarionMonacoEditor](https://github.com/msarson/ClarionMonacoEditor).)*

### Code Snippets in the editor

A full snippet workflow lands in the Monaco source editor and CA Embeditor:

- **Ctrl+J picker** &mdash; theme-aware, with a **live expansion preview** so you see the resulting code (tab-stops visible) before inserting.
- **Snippets in completion** &mdash; type a trigger and press **Enter** to expand (Clarion Ctrl+J parity), or use **trigger + space** for Clarion-style expansion.
- **Manage them in the gear panel** &mdash; edit snippets with examples/help, tab-stop mirroring, and case-insensitive extension scoping.

### Pure upstream language server (v0.9.8)

The bundled LSP is now **stock upstream** [msarson/Clarion-Extension](https://github.com/msarson/Clarion-Extension) **v0.9.8** &mdash; CodeGraph intelligence runs entirely on the CA (C#) side, so there is no patched server to drift out of date, and future upstream releases are a clean one-step re-pin.

### Removed

- The old **Open in CA Embeditor** right-click menu injection and its underlying native Windows-hook machinery have been retired &mdash; the native-command overlay replaces them entirely, with far less moving machinery in the IDE.

---

## What's New in v5.1

v5.1 brings a modern editing experience to the Clarion IDE: Monaco is now the default source editor, a Smart Formatter cleans up your code on demand, and the CA Embeditor and CA Explorer give you a fast, keyboard-friendly way to work with embeds and their data.

### Monaco is now the default Clarion source editor

Clarion source files (`.clw`, `.inc`) open by default in a Monaco-powered editor &mdash; syntax highlighting, code folding, **F12 / Ctrl+Click go-to-definition**, inline diagnostics, and completion &mdash; while keeping the same save and navigation behavior as the native editor.

- **Toggleable per surface** &mdash; turn the Monaco source editor or the Monaco embeditor off under **Options &rarr; Clarion Assistant &rarr; Editor Surfaces**, with an optional file-type filter. Changes apply the next time you open a file.

### Smart Formatter (Ctrl+I)

Press **Ctrl+I** in the Monaco source editor or CA Embeditor to reformat Clarion code &mdash; consistent indentation for structures (IF/LOOP/CASE/ACCEPT) and aligned data declarations. As-you-type aids apply light formatting while you write, and the formatter's behavior is configurable.

### Code Snippets (Ctrl+J)

Press **Ctrl+J** in the CA Embeditor to open a code-snippet picker &mdash; the same shortcut as the classic Clarion text editor's template picker, filterable by trigger/description and scoped to the active file's extension.

- **Managed from Settings &rarr; Snippets** &mdash; add/edit/delete snippets (trigger, description, extensions, body); stored globally in `%APPDATA%\ClarionAssistant\snippets.json`.
- **Tab-stops** &mdash; bodies use VS Code-style snippet syntax (`$1`, `${1:default}`, `$0`); **Tab**/**Shift+Tab** cycles between stops after insertion.
- **`${SELECTED}`** &mdash; substituted with the text selected when Ctrl+J was pressed, or left as a fillable tab-stop if nothing was selected.
- **Auto-indent** &mdash; continuation lines match the indentation of the line Ctrl+J was triggered on.

### CA Embeditor &mdash; right-click a procedure to open it instantly

Opening a procedure's embed code is a single right-click. In the Clarion application tree, right-click any procedure and choose **Open in CA Embeditor** &mdash; the procedure opens directly in a fast Monaco/WebView2 editor, with no Tools-menu or picker round-trip.

- **One-click from the app tree** &mdash; right-click the procedure &rarr; **Open in CA Embeditor**. The procedure you clicked is the one that opens.
- **Multiple procedures as tabs** &mdash; open several procedures side by side; each gets its own CA Embeditor tab.
- **Also available from the toolbar** &mdash; the **CA Embeditor** button on the native embed-editor toolbar opens the currently selected procedure.

### CA Explorer

A docked pad that shows the data behind the procedure you're editing. Open it from **Tools &rarr; CA Explorer** or with **Ctrl+Alt+D** (its docked title bar reads **CA Explorer**); it follows the active CA Embeditor tab.

- **Local, Module &amp; Global Data** &mdash; the variables in scope for the active procedure.
- **Declared Tables &amp; Other Files** &mdash; with their **Keys**, **Columns**, and **Relations**.
- **Follows the active tab** &mdash; switch CA Embeditor tabs and the pad updates to match.
- **Drag a field to the editor or designer** &mdash; drop a column or variable onto the Monaco editor or the native Window designer to create a bound control.
- **Copy / paste variables** native-style, plus a **Cheat Sheet tab** of Monaco editor shortcuts.

### Richer LSP in the Monaco surfaces

The Monaco source editor and CA Embeditor surface live error/warning squiggles and code completion directly as you type &mdash; including `PROP:`/`EVENT:` equates and member-access (`oInstance.Method`) suggestions &mdash; alongside F12 / Ctrl+Click go-to-definition.

### Fixed

- **Clean-install editor load on Clarion 12** &mdash; a strong-name assembly version-lock caused a `Cannot create object: MonacoClarionEditorDisplayBinding` error on C12 builds other than the dev build. The editor now loads on any Clarion 12 build, and installer packaging (Monaco assets and LSP contracts) has been corrected.

### Renamed: "Modern Embeditor / Modern Data" &rarr; "CA Embeditor / CA Explorer"

The feature previously shown as *Modern Embeditor* and *Modern Data* is now **CA Embeditor** and **CA Explorer** (Clarion Assistant) across the menus and UI. Same feature, clearer name.

---

## What's New in v4.6

### Custom MCP servers in the IDE pane (issue #26)

Users can now expose their own MCP servers to the in-IDE Claude tabs without modifying the addin, alongside the three built-ins (clarion-assistant, project-hub, browser).

- **`%APPDATA%\ClarionAssistant\mcp-extra.json` sidecar** &mdash; standard `mcpServers` JSON. Entries are merged into the generated mcp-config.json each time a Claude tab launches, so edits take effect on the *next* tab spawn without restarting the IDE.
- **Settings &rarr; MCP page** has a new "Custom MCP Servers in the IDE Pane" section with an **Open** button that creates the file from a commented template if missing, then shell-opens it. Always visible regardless of the External Access toggle.
- **Auto-approve** &mdash; `mcp__<server>__*` is appended to `--allowedTools` for every sidecar entry, so user tools don't trigger permission prompts inside the IDE pane.
- **Reserved-key protection** &mdash; an mcp-extra.json entry that collides with a built-in key (`clarion-assistant`, `project-hub`, `browser`) is silently skipped; the addin's real entry always wins.
- **Malformed-JSON resilience** &mdash; a corrupted sidecar (trailing comma, stray brace, etc.) is logged and ignored. The tab still launches with the three built-ins.
- **Empty-state safe** &mdash; if the file doesn't exist, behavior is identical to v4.5: just the three built-ins, no errors.

Smoke-tested end-to-end with `@modelcontextprotocol/server-everything` against all four corners: happy path, collision, malformed JSON, missing file.

### Codex config self-heal

Fixes a corruption mode where launching a Codex tab would error with `duplicate key [mcp_servers.clarion-assistant]` and refuse to start.

Codex CLI's TOML serializer silently drops trailing comments when it appends state tables (e.g. `[tui.model_availability_nux]`), which removed our `<<< end >>>` marker. The next CA launch then saw a begin marker with no matching end and appended a second managed block, producing duplicate `[mcp_servers.clarion-assistant]` tables.

- **Self-healing rewrite** &mdash; on every Codex launch CA now strips all CA markers (paired or orphan) and all `[mcp_servers.clarion-assistant.*]` sections anywhere in the file, lifts foreign tables out of any broken managed region, and appends one fresh canonical block. Even a config that's already broken (two begin markers, one end, duplicate tables) is cleaned up automatically on the next launch.
- **No user action required** &mdash; just relaunch a Codex tab and the file is repaired in place.

---

## What's New in v4.5

### External MCP client access (issue #24)

Lets external tools (Claude Desktop, Cline, custom mcp-remote setups) authenticate against ClarionAssistant's local MCP server using a stable user-managed token instead of the per-session token that rotates each IDE start.

- **New "MCP Server" Settings page** with a "Most users can skip this page" intro &mdash; the toggle is opt-in for users who want CA's IDE tools surfaced in tools outside the IDE.
- **Allow external MCP clients toggle** persists across IDE restarts. Off by default.
- **Generate button** mints a 64-character hex bearer token, stored in `settings.txt`. Idempotent: regenerating invalidates any external configs already using the previous token.
- **Live endpoint URL** display shows `http://localhost:<port>/mcp` (port is dynamically chosen at startup).
- **Pre-filled `mcp-remote` config snippet** with **Copy config** button &mdash; ready to paste into the external tool's MCP config.
- **Add to Claude Desktop button** &mdash; one-click integration: writes the `clarion-assistant` entry directly into `%APPDATA%\Claude\claude_desktop_config.json`, backs up the original to `.clarionassistant.bak`, atomic write. Idempotent (re-click after rotating token to push the update).
- **Dual-token authentication in `RequireAuth`** &mdash; the per-session token still authenticates in-IDE Claude Code / Copilot / Codex tabs; the static external token is checked additionally when external access is enabled. Both compared in constant time. Settings re-read on every request so toggle/rotation takes effect immediately.

### SettingsService cross-process hardening (collateral)

Strengthening the settings store to support the External MCP feature surfaced multiple latent issues that affect every settings save, not just MCP tokens:

- **Static lock + named mutex** (`Local\ClarionAssistant.SettingsService.v1`) serializes settings writes across all SettingsService instances within a process AND across multiple ClarionAssistant processes (the addin supports multi-IDE).
- **Reload-before-write merge** &mdash; rotating Mcp.ExternalToken in one place no longer gets reverted when an unrelated control later saves an unrelated setting.
- **Atomic file replace** via temp-file + `File.Replace` (ReplaceFileW on NTFS) so concurrent readers always see either the old file or the new file, never a half-written one.
- **Reload preserves in-memory state** when the file is missing or the read fails &mdash; transient delete-then-rename from sync tools / antivirus no longer wipes settings on the next save.
- **`SettingsLockedException`** is thrown when another CA process holds the cross-process mutex past the 5s budget; the settings dialog catches it and surfaces an actionable message instead of silently degrading.

### Pre-existing security hardening still applies

The MCP server still binds to loopback only (Windows OS blocks remote connections), validates Host and Origin headers (DNS-rebinding and browser-drive-by defense), and uses constant-time token comparison.

---

## What's New in v4.4

### OpenAI Codex CLI backend

Codex joins Claude Code and Copilot as a third launchable terminal backend.

- **Backend selection on the dashboard** &mdash; Codex is now a third option alongside Claude Code and Copilot. Tab labels show **CC** (Claude Code) / **CP** (Copilot) / **CO** (Codex).
- **MCP wiring via `mcp-remote` stdio bridge** &mdash; Codex CLI's native HTTP/SSE transport timed out against CA's Streamable-HTTP `/mcp` endpoint, so CA writes a marker-delimited block in `~/.codex/config.toml` that bridges through `mcp-remote`. Foreign tables Codex CLI itself appends inside the markers (e.g. `[tui.model_availability_nux]`) are lifted out and preserved on rewrite.
- **Pinned `mcp-remote` install** &mdash; CA refuses to write the Codex MCP config if `mcp-remote` isn't installed at the expected npm-global location, and refuses to wire it if the installed version doesn't match the pinned version. Closes a supply-chain risk where `npx -y mcp-remote` would auto-fetch from the public registry at launch with the live MCP bearer token.
- **Codex settings panel** &mdash; reasoning effort dropdown (Auto / low / medium / high), one-time install instruction for `mcp-remote`. Models populated from Codex CLI's actual `/models` list: GPT-5.5 (default), GPT-5.4, GPT-5.4 mini, GPT-5.3 Codex, GPT-5.2.
- **AGENTS.md preserved** &mdash; CA only writes the briefing file when one doesn't already exist in the working directory. Won't clobber a repo-tracked `AGENTS.md`.
- **Robust exception handling in the launch banner** &mdash; failure-reason text is sanitized (CR/LF, double quotes, single quotes, truncated to 240 chars) before splicing into the PowerShell `Write-Host` payload.

### Bundled installer ships with Codex support
Settings → MCP Server panel and the new sidebar entry are part of the standard install.

---

## What's New in v4.3

### Backend selection UX
- **Dashboard backend dropdown** &mdash; pick Claude Code or Copilot per click; overrides the saved default without rewriting it. "Reset to default" link appears whenever the dropdown differs from the saved default.
- **Per-card backend badges** on the quick-action cards (CLAUDE CODE / COPILOT) update live with the dropdown so you can see at a glance which backend each click will launch.
- **Tab-strip labels show the backend** &mdash; new tabs read "Terminal 1 CC" (Claude Code), "Terminal 2 CP" (Copilot), or "CO" (Codex placeholder, reserved for future). Custom-named tabs get the same suffix: "MyProject CC".

### Settings dialog redesign
- **Launch page is now first in the sidebar** and uses per-backend tabs (Launch / Models) under a dropdown
- **"Set as Default" button** replaces the implicit "saved when you change the dropdown" behavior &mdash; now you can inspect any backend's settings without accidentally changing the default
- **Working Directory and Projects Directory are per-backend** &mdash; live on the Models tab; each backend remembers its own paths
- **General page removed** &mdash; its global fields moved: Working Directory to Launch (Models tab, per backend), COM Projects Folder to Classes

### Unified assistant instructions
- **Copilot sessions now get the full 220-line Clarion IDE prompt** previously only Claude received. Both backends share a single authoritative `clarion-assistant-prompt.md`, deployed as `AGENTS.md` for Copilot.

### Security hardening
- **MCP HTTP server requires a per-session bearer token** &mdash; generated fresh each addin start and embedded in the MCP config file each CLI client reads. Drive-by browser fetches to `localhost:19372` no longer reach the tools surface.
- **CORS permissiveness removed** &mdash; `Access-Control-Allow-Origin: *` is gone; browser drive-by can't read responses.
- **Host and Origin header validation** blocks DNS-rebinding attacks and cross-origin browser requests.
- **pwsh launch payload now tokenizes and quotes user settings** &mdash; `Copilot.ExtraFlags`, Claude/Copilot command entries, and Copilot.Model go through a new `PwshCommandQuoter` helper. A crafted settings value can no longer chain arbitrary PowerShell commands.
- **Settings file newline rejection** &mdash; `SettingsService.Set` now throws on CR/LF in values (previously a `\n` could smuggle a second key on reload, e.g. stealth-enabling `Copilot.PermissionMode=allow`).

### Reliability
- **DocGraph FTS5 load fixed** &mdash; `query_docs` no longer intermittently returns "database disk image is malformed" on valid data. The extension is now loaded from an absolute path (no more `LoadLibrary` search-order roulette), and a smoke probe at first use surfaces a clear diagnostic error if the FTS5 module fails to register.
- **New `rebuild_docgraph_fts` MCP tool** &mdash; in-place recovery if someone lands on a corrupted FTS shadow index. Drops and recreates the FTS table from the intact `doc_chunks` content.

### Internals (visible in debug logs)
- **Renamed `ClaudeChatControl` &rarr; `AssistantChatControl`** to reflect its dual-backend role
- **Shared launch scaffolding** &mdash; `LaunchClaudeForTab` and `LaunchCopilotForTab` now share `PrepareBackendLaunch`, `StartTabTerminal`, `AbortLaunch` helpers so adding a 3rd backend (Codex etc.) is a matter of writing one command-body builder, not re-copying ~60 lines of scaffolding

---

## What's New in v4.2

### GitHub Copilot CLI Backend
- **New Assistant Backend setting** (Settings → Launch) — switch between Claude Code and GitHub Copilot CLI per terminal session
- **Copilot Commands** — configurable launch commands for Copilot (same Add/Edit/Delete/Set Default UI as Claude commands), with defaults: `copilot`, `gh copilot`, `copilot --allow-all-tools`
- **Copilot Model selector** (Settings → General) — dropdown with all available Copilot models grouped by provider (GPT-5.x, Claude 4.x); model is passed via `--model` flag at launch
- **Permission Mode** — choose between `Prompt` (default) or `Allow Tools` (`--allow-all-tools`)
- **Extra Flags** — optional additional flags appended to the Copilot command
- **MCP integration** — Clarion Assistant MCP server auto-configured for Copilot with `tools: ["*"]` format; custom instructions deployed via `COPILOT_CUSTOM_INSTRUCTIONS_DIRS`
- **Per-tab backend tracking** — each tab remembers its backend (`AssistantBackend` property) so status bar and exit handling work correctly even if the setting changes mid-session

### Build improvements
- Explicit WebView2 assembly references in `.csproj` to fix compile-time resolution under MSBuild
- `ValidateClarionReferences` MSBuild target for clearer error messages when `ClarionRoot` is not set
- `deploy.ps1` now skips the indexer build gracefully when the project is not present

---

## What's New in v4.1

### Auto-Update Claude Code
- **New setting in Launch tab** &mdash; optional toggle to run `claude update` before each terminal session starts
- Ensures you're always on the latest Claude Code version without manual checks

### DocGraph
- **BoxSoft documentation added** &mdash; BoxSoft template documentation now indexed and searchable via `query_docs`

### Bug Fixes
- **Fixed: GitHub icon URL** &mdash; header toolbar GitHub button now correctly links to the [clarionlive/clarionassistant](https://github.com/clarionlive/clarionassistant) repository

---

## What's New in v4.0

### Clarion Language Server (LSP)
- **Bundled in the installer** &mdash; real-time code intelligence with zero setup
- **9 LSP tools** &mdash; go-to-definition, find references, hover info, document symbols, workspace symbol search, diagnostics, and rename support
- **Ships with Node.js runtime** and all required dependencies &mdash; no separate install needed
- **`/lsp-diagnostics` skill** &mdash; run diagnostics across every source file in the open solution with navigate-to-error support
- **LSP Status Bar** &mdash; live language server status displayed on each terminal tab

### Schema Graph Database
- **Per-project SQLite schema index** for Clarion dictionaries and SQL databases
- **.dctx XML parser** &mdash; extracts tables, columns, keys, and relationships from Clarion dictionary exports
- **SQL Server extraction** &mdash; tables, columns, keys, foreign keys, stored procedures, functions, and views
- **Merge logic** &mdash; matches SQL tables to existing dictionary tables by name, adds SQL-only tables
- **FTS5 full-text search** &mdash; fuzzy name search across tables and columns
- **10 MCP tools** &mdash; `ingest_schema`, `ingest_sql_database`, `search_tables`, `get_table`, `search_columns`, `get_relationships`, `query_schema`, `schema_stats`, `export_dctx`, `import_dctx`

### 108 MCP Tools
- Up from ~30 documented tools in v3.1 to **108 fully documented tools** across 12 categories
- New categories: LSP, File System & Search, Multi-Instance Coordination, Validation

### Installer
- **Code-signed** with Sectigo EV certificate (Kennewick Computer Company)
- **22 Clarion development skills** (up from 17)
- **LSP server** distributed as an optional component for Clarion 10, 11, and 12
- Updated prerequisites &mdash; optional [Everything](https://www.voidtools.com) integration for fast file search

---

## What's New in v3.1

### Create Class from Model Templates
- **New "Create Class" tab** &mdash; select a model template, name your class, and generate .inc/.clw files in one step
- **Class Models settings** &mdash; manage model templates in Settings &rarr; Classes, with Edit (opens both .inc and .clw) and Delete
- **Class output folder** &mdash; configurable default output folder for generated class files
- **Syntax-highlighted preview** &mdash; preview .inc and .clw content before creating, with Clarion syntax highlighting

### Status Line
- **Live status bar** on each terminal tab showing model name, usage quota, pacing, and context window fill
- **Polls from Claude Code** via a statusLine hook script (`ca-statusline.js`) that writes JSON to a temp file

### Embeditor Navigation
- **`list_embeds`** &mdash; new MCP tool to list all embed sections in the active embeditor with filled status
- **`find_embed`** &mdash; new MCP tool to search embed sections by partial name and navigate the cursor there

### Project Info
- **`get_ca_project_info`** &mdash; new MCP tool to look up linked GitHub account and repo name for a project folder

### Claude Code Detection Improvements
- **Standalone CLI and WinGet support** &mdash; finds `claude.exe` from standalone install (`~/.claude/local/`), WinGet (`AppData/Local/Microsoft/WinGet/Links/`), npm global, or PATH
- **Installer detection updated** &mdash; checks all install locations before falling back to PATH; install message now suggests `winget install Anthropic.ClaudeCode`

### UI Improvements
- **Removed Settings from home page** &mdash; use the gear icon button instead
- **Light/dark theme** correctly applied to the Create Class tab on creation

---

## What's New in v3.0

### Schema Sources &mdash; Database Intelligence
- **Schema Source Manager** &mdash; collapsible "Solution Settings" panel above the terminal with per-solution database source linking
- **Multi-database support** &mdash; index schemas from Clarion dictionaries (.dctx), SQL Server, SQLite, and PostgreSQL
- **Global source registry** &mdash; define database sources once, link to multiple solutions
- **DPAPI-encrypted credentials** &mdash; connection info stored securely with Windows data protection
- **Test Connection** &mdash; validate database connections before indexing
- **MCP integration** &mdash; Claude automatically finds and queries your indexed schemas via `search_tables`, `get_table`, `get_relationships`, and more

### Source Control Integration
- **GitHub and Bitbucket** accounts with encrypted token/app password storage
- **Per-solution repo linking** &mdash; assign a source control account + repo to each solution
- **Test authentication** &mdash; validates credentials against GitHub/Bitbucket APIs

### Simplified Home Page
- **Quick Actions** &mdash; New Chat, Evaluate Code, Settings, and Work With Open Solution cards
- **Projects table** &mdash; manage COM controls, addins, and other projects with GitHub/Bitbucket repo linking
- **Removed Solutions tab** &mdash; the IDE manages solutions; Clarion Assistant auto-detects the open solution

### Multi-Version Installer
- **Supports Clarion 10, 11, and 12** &mdash; install to one or all versions from a single installer
- **Per-version installation** &mdash; installing for one version won't affect another
- **Auto-detection** &mdash; finds Clarion installations from the Windows registry and common paths
- **Browse buttons** &mdash; pick custom Clarion paths for non-standard installations

### Evaluate Code Improvements
- **5 scope options** &mdash; evaluate the entire app, a specific procedure, embeditor content, text editor file, or selected code
- **Smart file detection** &mdash; correctly identifies text files vs .app files in the IDE
- **No fabricated results** &mdash; always reads real code from the IDE before producing any evaluation

### Bug Fixes
- **Fixed: Empty 404 crashes Claude Code SDK** &mdash; MCP server now returns JSON body on 404 responses during OAuth discovery ([#3](https://github.com/peterparker57/ClarionAssistant/issues/3))
- **Fixed: replace_text destroys embeditor content** &mdash; now uses surgical `Document.Replace()` instead of full-document replacement ([#2](https://github.com/peterparker57/ClarionAssistant/issues/2))
- **Fixed: LSP server path hardcoded** &mdash; resolved relative to assembly location with configurable override ([#1](https://github.com/peterparker57/ClarionAssistant/issues/1))
- **Fixed: Solution not auto-detected** &mdash; 10-second polling detects solution changes in the IDE
- **Fixed: DocGraph personal search crashes** &mdash; FTS5 virtual tables can't use schema-qualified names; queries now run independently and merge results
- **Fixed: Header/action bar text clipping** &mdash; responsive flex-wrap layout

### Other Improvements
- **Zoom persistence** across all WebView2 panels (header, home, settings, schema sources)
- **Responsive header** &mdash; text wraps instead of clipping at narrow widths
- **Import Now button** for personal DocGraph documentation with progress feedback
- **Remove All Personal** button for bulk DocGraph cleanup
- **Delete confirmation** for source control accounts

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

### Install

1. **[Download the latest installer](https://github.com/ClarionLive/ClarionAssistant/releases/tag/v5.3.0)** (code-signed)
2. Close the Clarion IDE
3. Run the installer &mdash; select which Clarion versions to install for
4. Restart the Clarion IDE

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

[MIT License](LICENSE) &mdash; &copy; 2026 ClarionLive.
