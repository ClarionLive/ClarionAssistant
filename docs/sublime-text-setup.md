# Clarion in Sublime Text

For Clarion developers who prefer to hand-code and would rather not run the Clarion IDE.

**Nothing here requires the Clarion IDE to be running.** That is the point of this document, and it
is the constraint everything below was chosen against.

> **STATUS: PARTIALLY VERIFIED.** Ticket 92d3ad8e. The server side is proven — see
> [What was actually verified](#what-was-actually-verified). The Sublime side is **written from
> verified inputs but has not been run in Sublime**, because there is no Sublime install on the
> machine this was written on. Treat section 2 as a well-founded first draft, not a tested recipe.
> Whoever has Sublime should run it, correct it, and delete this banner.

---

## What you get, and what you don't

| | Works without the IDE | How |
|---|---|---|
| Completion, go-to-definition, hover, find-references | **yes** | language server, section 2 |
| Rename, document symbols, folding, formatting, signature help | **yes** | language server, section 2 |
| Clarion knowledge: syntax, ClarionCOM, WebView2, jFiles, StringTheory | **yes** | Claude Code plugin, section 1 |
| AI chat inside the editor | **yes** | third-party plugin, section 3 |
| CodeGraph, DocGraph, SchemaGraph, build tools | **no — not yet** | see [Not available yet](#not-available-yet) |
| Embeditor, app tree, designers, `.app`/`.dct` editing | **never** | see [Never available](#never-available) |

---

## 1. Clarion knowledge in Claude Code

Two commands, and a large body of Clarion-specific guidance becomes available to Claude Code in any
terminal — editor-agnostic, no IDE, no Clarion Assistant install.

```
/plugin marketplace add ClarionLive/clarionassistant-marketplace
/plugin install clarion-assistant
```

> ⚠ **Confirm the exact second command against your Claude Code version before relying on it.** It
> has not been re-run against the current CLI. If it differs, fix it here.

> ⛔ **Do not publish to that marketplace right now.** Two unresolved drift problems are tracked on
> tickets 69b2f1fb and 0eabb5a0. Installing *from* it is fine; publishing *to* it would currently
> overwrite content that exists only on GitHub.

This ships skills covering Clarion syntax and idiom, ClarionCOM control creation and deployment,
WebView2 components, jFiles JSON, StringTheory, code evaluation, and LSP diagnostics.

## 2. Language features via the Clarion language server

The language server is a plain stdio process. Any editor with an LSP client can drive it, which is
how the VS Code extension works too — it is a build of
[msarson/Clarion-Extension](https://github.com/msarson/Clarion-Extension) with Clarion Assistant's
CodeGraph overlay applied.

### 2a. Install the two Sublime packages

Via Package Control:

- **`LSP`** — the language-client package.
- **`Clarion`** — syntax highlighting, which the LSP client needs in order to know which files to
  attach to. Source: [fushnisoft/SublimeClarion](https://github.com/fushnisoft/SublimeClarion). It
  defines the scope `source.clarion`, which is what section 2b selects on.

> ⚠ **The `Clarion` package was last updated around 2017 and is tagged ST2/ST3, not ST4.**
> `.tmLanguage` syntaxes normally still load in ST4, so this is expected to work — but it is
> unmaintained and untested here. If it does not load, the LSP client has no scope to attach to and
> nothing in section 2 will fire. That is the single most likely failure point in this document.

### 2b. Point the LSP client at the server

`Preferences → Package Settings → LSP → Settings`:

```json
{
  "clients": {
    "clarion": {
      "enabled": true,
      "command": [
        "C:\\Clarion12\\accessory\\addins\\ClarionAssistant\\lsp-server\\node.exe",
        "C:\\Clarion12\\accessory\\addins\\ClarionAssistant\\lsp-server\\out\\server\\src\\server.js",
        "--stdio"
      ],
      "selector": "source.clarion"
    }
  }
}
```

**Adjust the Clarion version in both paths** to whichever install you have — `C:\Clarion11`,
`C:\Clarion11.1`, and so on. The layout under `accessory\addins\ClarionAssistant\lsp-server\` is the
same in each.

Note the server ships **its own `node.exe`** beside it, so you do not need Node installed. Using the
bundled one is deliberate: it is the version the server was built and tested against.

### 2c. What the server advertises

From a live `initialize` handshake against the bundled server (see
[verification](#what-was-actually-verified)):

```
completion (trigger chars . and :)   definition            hover
references                           documentSymbol        rename (with prepare)
formatting + range formatting        foldingRange          signatureHelp ( , )
semanticTokens                       codeAction            codeLens
workspaceSymbol                      implementation        documentHighlight
color                                selectionRange        documentLink
```

Whether Sublime surfaces all of these depends on the `LSP` package, not on the server.

---

## 3. AI chat inside Sublime — optional, third party

[`tommo/sublime-claude`](https://github.com/tommo/sublime-claude) puts a Claude Code session in a
Sublime output view: an embedded terminal adapted from Terminus, plus a bridge process speaking
JSON-RPC over stdio to the Claude Code CLI. It supports MCP.

**We do not maintain, bundle, or support this.** As of 2026-09-01 it is actively maintained (last
push 2026-08-17) but small — 22 stars, 5 forks — and **its license is unrecognised by GitHub**
(`NOASSERTION`). Fine to try; understand what you are depending on. It needs Python 3.10+ for the
bridge, separate from Sublime's own bundled Python.

A plain terminal running `claude` beside Sublime does the same job with nothing extra installed.

---

## Not available yet

CodeGraph, DocGraph, SchemaGraph and the build tools are MCP tools **hosted inside the Clarion
Assistant addin**, so today they exist only while the Clarion IDE is running — which defeats the
purpose for anyone reading this.

Extracting them into a standalone server process is tracked as **d051fbd1**. When that lands, these
become available with no IDE, and the natural delivery is the plugin from section 1 gaining an
`mcpServers` entry — so the same two commands would bring the tools as well as the skills.

## Never available

The app tree, the embeditor, PWEE embed slots, `.app`/`.dct` manipulation, the window and report
designers, and Errors-pane routing will not come to another editor. They are not "Clarion IDE
integration" that could be reimplemented — they drive Clarion's own 32-bit assemblies against a
proprietary binary format. Hosting them elsewhere would mean building a headless Clarion.

If you hand-code rather than using the app generator, you have already opted out of all of it.

---

## What was actually verified

Recorded so the next reader knows which claims are load-bearing and which are inference.

**Verified on 2026-09-01, with Clarion confirmed not running:**

- The language server starts standalone and completes a real LSP `initialize` handshake. Spawned
  exactly as an editor would: `node server.js --stdio`.
- It advertised the 20 capabilities listed in 2c. That list is copied from the handshake response,
  not from documentation.
- `stderr` carried only a startup line — no errors, and **no complaint about a missing CodeGraph
  database**, so the server does not require one in order to start.
- The paths in 2b exist on disk, including the bundled `node.exe` (~90 MB).
- `source.clarion` is the scope name, read from the syntax definition in the package's own source
  rather than assumed.
- A Clarion syntax package for Sublime exists on Package Control (507 installs).

**NOT verified — do not present these as tested:**

- That the section 2b config works in Sublime. There is no Sublime install on the authoring machine.
- That the 2017-era `Clarion` package loads in ST4.
- Whether individual features **degrade at query time** without a CodeGraph database. Only startup
  was probed. Completion and go-to-definition may behave differently on a workspace that has never
  been indexed.
- The exact `/plugin install` syntax in section 1 against the current Claude Code CLI.
