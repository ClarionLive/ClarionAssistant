# ClarionAssistant test harnesses

```powershell
.\tests\Run-Tests.ps1            # everything
.\tests\Run-Tests.ps1 -Probe     # + the read-only live VS Code probe (diagnostic)
```

One entry point, three families. None is wired into MSBuild — see *Why nothing runs at build time* below.

## The families

| | `tests\*.cs` | `Terminal\test\*.test.js` | `tests\*.ps1` |
|---|---|---|---|
| Under test | C# service code with **no IDE coupling** | the Monaco WebView2 pages | the standalone MCP server, as a real process |
| How | standalone `csc` compile of the real source | node, mostly zero-dependency | build the real `.exe`, drive it over stdio |
| Runs where | anywhere, no Clarion needed | anywhere, no Clarion needed | anywhere; some need node + the Clarion LSP |

All three read or build the **real production source** rather than a copy of it. That is the property
worth protecting: these harnesses are only as valuable as their inability to drift from the thing
they describe.

The `.ps1` family exists for behaviour that only appears once the code owns a real process and talks
to something it does not control — a console-encoded stdio stream, or a language server that answers
in its own time. Neither survives being stubbed, because a stub author decides the very thing under
test.

## What's here

| File | Guards |
|---|---|
| `VsCodeSettingsImporter.SmokeTest.cs` | JSONC stripping (comments, trailing commas, a `//` *inside* a string literal), the `[clarion]` language-scope override beating the global, CSS font-stack first-family extraction, enum coercion and clamping, missing/corrupt files |
| `VsCodeSettingsImporter.PayloadCheck.cs` | the bridge response shape the page reads — including two distinctions that are easy to collapse: `error` is `""` not `null` on success, and `cancelled` stays separable from not-found |
| `VsCodeSettingsImporter.LiveProbe.cs` | *(diagnostic, opt-in)* runs the importer against this machine's real VS Code install. Read-only; never echoes the file's contents |
| `..\Terminal\test\monaco-page-integrity.test.js` | NUL bytes and syntax damage in the Monaco pages — both invisible in a diff and both survive a clean build |
| `..\Terminal\test\vscode-import-ui.test.js` | the gear panel's VS Code import UI, driven from markup and JS **extracted from the page at run time** |
| `..\Terminal\test\clarion-folding.test.js` | the shared Clarion folding provider (GH #158, #133) |
| `..\Terminal\test\clarion-formatter.test.js` | the Smart Formatter |
| `..\Terminal\test\mark-word.test.js` | GH #229: Mark Word (Ctrl+W) selecting the `[A-Za-z0-9_]` word at every cursor, `LOC:Name` as two words, a second press widening to the double-click word under the real Clarion `wordPattern`, and its `EDITOR_COMMANDS` entry being the only Ctrl+W default and not handed to the IDE |
| `..\Terminal\test\schema-sources-postgres.test.js` | GH #201: the Schema Sources *Index* cell showing the real error rather than the word `error` (handler extracted from the page), and the PostgreSQL procedure query excluding aggregates (`prokind <> 'a'`) but not window functions |
| `..\Terminal\test\f12-keys.test.js` | 77aceec5: the CA Editor's F12 family (interceptor extracted from the page) - F12 goes to definition, Ctrl+F12 to implementation, and Shift+F12 no longer silently goes to definition |
| `..\Terminal\test\buffer-sync.test.js` | 16d140e9: the Monaco page ships its buffer to the host once per content version (the real request code extracted from the page) - every buffer-needing request (completion, hover, signature help, folding, diagnostics, F12, Ctrl+F12, Ctrl+click, outline, designer) sends `v` not `buffer`, preceded by exactly one `bufferSync` after an edit and none without one; a model swap / `resetBufferSync` forces a resend; file mode's `fileState` doubles as the sync; diagnostics' timeout grows with the buffer and only one request is in flight. Prints the bytes posted for two scenarios on a 3.2 MB buffer; red against the pre-fix page |
| `..\Terminal\test\local-first-completion.test.js` | 1c685f2e item 5: two-phase completion (the real provider extracted from the page, with a hand-driven host and timers) - the local list shows when the LSP misses its 200 ms budget (incomplete:true), the LSP answer is cached on (model.id, line, wordStart) and reused while the typed prefix extends it (filtered client-side), 3 misses on a model skip the race and a fast reply ends skip mode, case-insensitive dedupe with the local item winning, overloads kept, the 00_/0kw_/1_ sort tiers, setSource empties the cache, and nothing re-triggers suggest. Shares `local-first-loader.js` |
| `MonacoBufferSync.Test.cs` | 16d140e9: the host half - `MonacoBufferCache` (a matching `v` returns the cached instance, a mismatch null, a newer sync replaces), request resolution (inline / cached / missing / none), the sync-message parser (text-last fast path, every JSON escape, fallback shape, a 3.2 MB buffer), and `LatestOnlyWorker` (one job at a time, only the newest waiting job runs, displaced ones are answered) |
| `EmbedLspContext.LineMapping.cs` | the CA Embeditor's Monaco-to-LSP line mapping agreeing with what `WrapBuffer` actually prepended: every line of the usual embed shape (blank lines, then MEMBER) and of a buffer opening with MEMBER or PROGRAM on line 1 (passed through, offset 0) maps to the same text and back. Before the fix the offset was a constant 1, so a pass-through buffer's completion asked the server about the line below the caret. Real `EmbedLspContext.cs`, IDE capture stubbed |
| `McpDispatcher.UiTimeout.Test.cs` | PR #198: the UI-thread tool timeout through the REAL `McpDispatcher.cs` (registry stubbed): `McpUiTimeoutPolicy` default 30s / setting / per-tool declared minimum / [5, 600] clamp, a configured 5s budget timing out a 7s UI tool while a declared 10s one finishes, the abandoned call refused at its commit point (and a never-started one never running, a committed one reported "may still complete"), a throwing settings reader treated as unset, and a source scan that the real registry still declares the 180s budget on the four embed round-trip tools |
| `EmbedApplyFlow.Test.cs` | PR #198: `apply_embed_edits` after the embeditor is open (IDE ops faked) - a call `McpDispatcher` abandoned on timeout rolls back instead of saving, one already committed saves; a failed save, unconfirmed close or write error discards our writes for adopted and self-opened editors alike; nothing is written when abandoned before writing |
| `..\Terminal\test\explorer-header.test.js` | 16d140e9: the CA Explorer header's APP / VERSION / ROOT / RED lines (renderer extracted from `modern-data-pad.html`) - full paths, SOLUTION label when no app is open, a dash and no click for unknown values, a click posting only `which` (never a path), and the host's `openHeaderPath` handler reading only `which` and launching `explorer.exe` directly after validation |
| `ExplorerHeader.Test.cs` | 16d140e9: `ExplorerHeader.Compose` (open .app beats the solution, SOLUTION fallback, a bare app name placed beside the .sln, blank -> unknown) and `TryBuildExplorerArgs` - drive and UNC paths quoted, a drive root passed bare, and relative, device-namespace, URL, stream, wildcard, quote, control-char and non-existent paths refused, each also with existence probes that say yes |
| `EmbedAdoptPolicy.Test.cs` | PR #198: `apply_embed_edits` adopting an already-open embeditor only when it is the same procedure, its native `IsDirty` is explicitly false, and no CA Embeditor (overlay or live tab) holds it; the overlay refusal wins over the dirty one because Monaco's edits never reach the native flag |
| `NpgsqlLoader.SmokeTest.cs` | GH #188: only a *missing* Npgsql.dll is reported as not found; a present-but-broken one (a garbage `Npgsql.dll` is dropped beside the exe, loaded in a fresh AppDomain) surfaces the loader's own message |
| `McpStdio.EndToEndTest.ps1` | the standalone MCP server's stdio transport as a real process — the stdout hijack, UTF-8-no-BOM on the real handle, and clean exit on stdin EOF, none of which `--selftest-stdio` can see |
| `McpFileTools.EncodingTest.ps1` | `write_file` / `append_to_file` keeping Clarion source in its own encoding (GH #203): cp1252 stays cp1252, UTF-8 stays UTF-8, new and all-ASCII files are ANSI, a char the code page can't hold is refused with the file untouched. Fixtures are raw bytes in BOTH encodings; exits 2 unless the system ANSI code page is 1252 |
| `LspDiagnostics.SemanticPassTest.ps1` | `lsp_diagnostics` reporting a file **clean** on the first query while the server had sent only its synchronous pass (b7505691), and, since GH #216, while the server had DEFERRED the semantic pass - the first query must wait for `clarion/diagnosticsStatus: complete`. Runs against the pinned bundled server (`.lsp-build\<currentPin.tag>`, found in this checkout or the main one) when it is built, else whatever resolves. Fixture: `fixtures\lsp-semantic-pass\` |
| `LspDiagnostics.StatusGateTest.ps1` | GH #216 contract: the real MCP server against a SCRIPTED language server (`fixtures\lsp-status-gate\fake-lsp-server.js`, via a child-only `VSCODE_EXTENSIONS`). `deferred` then `complete` is waited out past the settle window; a `complete` for another uri or an older version does not release the wait (pending:true at the budget); `superseded` then a newer version's `complete` is accepted; and a server that never sends `diagnosticsStatus` keeps the old symbolsRefreshed/settle fallback. Needs node |
| `LspStart.WorkspacePathTest.ps1` | 77aceec5: `lsp_start` **using** `workspace_path` (a .sln, or a folder with exactly one; several or none refused), and every LSP start path saying *why* it did not start (no solution / no server.js) instead of blaming a handshake that never ran. Also the plain-Chat fallback: a server launched with `--ide-pid` and no `--solution` starts on the solution the IDE published, and ignores a dead IDE's record. Needs no language server: the single-.sln cases accept any outcome that names the solution |
| `LspStart.FollowIdeSolutionTest.ps1` | 77aceec5 (pipeline B2): a server launched by the IDE with no `--solution` FOLLOWS the published solution - A to B restarts on B, closing it stops the server and reports no solution, an unchanged record does not restart, and a LOCKED record (Publish mid-copy) keeps the server rather than stopping it. Driven interactively; needs node + the Clarion extension (exit 2 otherwise) |
| `IdeSolutionRecord.Test.cs` | 77aceec5: the addin publishing the IDE's open solution for the standalone server it launched (plain-Chat LSP fallback): round trip, closed solution removes the record, a dead IDE pid or a deleted .sln is never handed on; a payload naming another pid is refused; ReadCached follows A to B to gone, and a locked or half-written record is TRANSIENT (last definite answer kept) |
| `LspReferences.DocumentOpenTest.ps1` | 77aceec5 item 5: `lsp_references` opening the document before asking. Without the didOpen the server answered null for an unopened file and the CodeGraph fallback's answer was returned instead; the first request on a fresh server must return the MAP line, the implementation and the call site. No CodeGraph db is staged, so the fallback cannot mask it. Needs node + the Clarion extension (exit 2 otherwise) |
| `CompletionMerge.DuplicateTest.ps1` | GH #187 (a): member completion listing the same method twice. The REAL `SharedLspBridge.GetCompletion` (built into the standalone server) over a real `LspClient`, against a stand-in server (`fixtures\completion-dup\fake-lsp.js`) that answers with a fixed list naming two members twice, plus a synthetic `.codegraph.db` for the member-access merge. Each member is listed once and keeps its detail, overloads (same insertText, different signature) both survive, the CodeGraph merge re-adds nothing the server listed and still adds a member only it knows. Needs node.exe where `LspClient` looks for it (exit 2 otherwise) |
| `CodeGraphReferences.FallbackTest.ps1` | 77aceec5 item 5: the CodeGraph fallback behind `lsp_references` (`CodeGraphReferences.Fallback.cs`, synthetic x86 SQLite db shaped like a real index): callers found through the IMPLEMENTATION row when the name lookup lands on the MAP prototype; ISOLATION from the request position (pipeline B1) - the same procedure name in two projects answers with the requester's project only, and PRECISION OVER RECALL (pipeline run 2) - unprovable visibility returns EMPTY: no position, a name declared only in another project, a file in two projects or with no project_id unless exactly one candidate exists; module data only from its own file; and LOCALS ARE NEVER RETURNED (cut in 77aceec5 - including a local used in a local-class method, and a local that shadows a project global, the negative control); a same-named row of another kind kept out, the name's real width instead of a zero-width column-0 range, and the on-disk path case instead of the index's lowercased copy |

## Dependencies

The C# harnesses need only `csc.exe` from the .NET Framework, which is present on any machine that
can build this addin.

The `.ps1` harnesses need MSBuild (they build the server under test every run, so a source edit
cannot be masked by a stale `.exe`). `LspDiagnostics.SemanticPassTest.ps1` additionally needs `node`
on PATH and msarson's Clarion extension installed, because the race it guards only exists between
two notifications from that real server (it prefers the pinned bundled build under `.lsp-build`; see its row above). Without them it exits **2** — *could not run* — rather than
passing: a machine that never started a language server has proven nothing about diagnostics.

The node harnesses are zero-dependency **except** `vscode-import-ui.test.js`, which needs `jsdom` —
the page code it exercises manipulates a real DOM, and shimming that would mean writing an HTML
parser. One time:

```powershell
npm install --prefix ClarionAssistant\Terminal\test
```

`jsdom` is declared in `Terminal\test\package.json` as a devDependency and is gitignored. Nothing
here ships with the addin; the pages have no npm dependencies at runtime.

If it isn't installed, that test exits **2** and `Run-Tests.ps1` reports it as *could not run* and
fails the overall run. A test that could not run is not a test that passed, and the runner will not
let a missing dependency read as green.

## Why nothing runs at build time

These exist to be run by a developer who just changed something, before they deploy. The bugs they
were written for — a NUL byte in a 420 KB HTML file, a settings panel that reads fine in dark mode
and is illegible in light — are all things that pass a build and fail a human. Wiring them into
MSBuild would slow every build without catching anything a pre-deploy run wouldn't.

Run them before you deploy.

## Adding to them

Keep new harnesses standalone and dependency-light, and keep them pointed at real source. If you
find yourself copying production logic into a test so the test can run, that is the signal the
production code has grown a coupling worth removing instead.

For the C# side specifically: `VsCodeSettingsImporter` is testable like this *because* it has zero
IDE references. If a service gains a reference to `MonacoEditorControl` or anything in the IDE object
graph, its harness stops compiling and the only way left to test it is clicking around a running
Clarion. Push the IDE-coupled part out into a thin bridge (see `Terminal\VsCodeImportBridge.cs`) and
leave the logic testable.
