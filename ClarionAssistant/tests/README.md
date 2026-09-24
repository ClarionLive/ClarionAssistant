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
| `..\Terminal\test\schema-sources-postgres.test.js` | GH #201: the Schema Sources *Index* cell showing the real error rather than the word `error` (handler extracted from the page), and the PostgreSQL procedure query keeping its `prokind IN ('f','p')` filter |
| `McpStdio.EndToEndTest.ps1` | the standalone MCP server's stdio transport as a real process — the stdout hijack, UTF-8-no-BOM on the real handle, and clean exit on stdin EOF, none of which `--selftest-stdio` can see |
| `LspDiagnostics.SemanticPassTest.ps1` | `lsp_diagnostics` reporting a file **clean** on the first query while the server had sent only its synchronous pass (b7505691). Fixture: `fixtures\lsp-semantic-pass\` |

## Dependencies

The C# harnesses need only `csc.exe` from the .NET Framework, which is present on any machine that
can build this addin.

The `.ps1` harnesses need MSBuild (they build the server under test every run, so a source edit
cannot be masked by a stale `.exe`). `LspDiagnostics.SemanticPassTest.ps1` additionally needs `node`
on PATH and msarson's Clarion extension installed, because the race it guards only exists between
two notifications from that real server. Without them it exits **2** — *could not run* — rather than
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
