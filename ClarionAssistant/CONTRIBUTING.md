# Contributing to ClarionAssistant

Thanks for your interest in contributing. ClarionAssistant is a SharpDevelop (#develop) add-in
that embeds inside the Clarion IDE (Clarion 10 / 11 / 12). This document explains how the pieces
fit together and how to rebuild the parts that are *not* plain C# in this repo — specifically the
bundled **LSP server** and the **clarion-indexer** — so you can build and deploy from a clean
checkout.

> Background for this doc: GitHub [#30](https://github.com/ClarionLive/ClarionAssistant/issues/30)
> (contributor onboarding / de-hard-coding) and [#40](https://github.com/ClarionLive/ClarionAssistant/issues/40)
> (LSP snapshot drift).

## Repository layout (high level)

| Path | What it is |
|------|------------|
| `ClarionAssistant.csproj` | The add-in itself (C#, .NET Framework 4.7.2, x86). |
| `Services/`, `Terminal/`, `Dialogs/`, … | Add-in source. |
| `CodeGraph/` | The CodeGraph layer **as used by the add-in** — this copy is IDE-coupled (e.g. `SourceResolver.cs` depends on `ClarionAssistant.Services.RedFileService`). |
| `indexer/` | **Vendored, self-contained** `clarion-indexer` console tool (see below). |
| `lib/sqlite-fts5/` | SQLite native DLLs with FTS5 enabled, deployed alongside the add-in. |
| `deploy.ps1` | Build + deploy script (the canonical way to build everything and copy it into a Clarion install). |

## Prerequisites

- **Visual Studio 2022** (any edition) with the MSBuild component. `deploy.ps1` finds MSBuild via
  `vswhere`, so you do **not** need to hard-code a VS path.
- **A Clarion install** (10, 11, and/or 12) to deploy into. The add-in folder is
  `<ClarionRoot>\accessory\addins\ClarionAssistant`.
- **Node.js** — only needed at *build/deploy* time, to bundle `node.exe` for the LSP server (end
  users do **not** need Node installed; we ship a copy). `deploy.ps1` finds it on `PATH`.

## Building and deploying

```powershell
# Build all configured Clarion versions and deploy. Close the Clarion IDE first (it locks the DLL).
.\deploy.ps1 -Version 12          # or 10 / 11 / all
.\deploy.ps1 -Version 12 -NoBuild # re-copy without rebuilding
```

`deploy.ps1` builds the add-in, builds the vendored `indexer/`, and copies the add-in + indexer +
LSP server into the Clarion install's add-in folder.

### Environment variables (override the defaults — no hard-coded machine paths)

The build/deploy script reads these. All have sensible fallbacks, so on a fresh machine you only
set the ones that differ from the defaults:

| Variable | Purpose | Default / fallback |
|----------|---------|--------------------|
| `CLARIONLSP_ROOT` | Local snapshot of the **LSP server** build output (`out/server`, `out/common`, `node_modules`). See *LSP server source* below. | `H:\DevLaptop\ClarionLSP` (legacy dev path) |
| `CLARIONLSP_NODE` | Explicit path to the `node.exe` to bundle with the LSP server. | `node` on `PATH`, else `C:\Program Files\nodejs\node.exe` |
| `CLARIONINDEXER_DIR` | Location of the indexer project, if you keep it outside the repo. | `<repo>\indexer` (the vendored copy) |

## The LSP server (and where its source lives)

The bundled LSP server (`lsp-server\out\server\...`) is **not** authored in this repo. It is a build
artifact of the public **Clarion VS Code extension**:

- Source: **[msarson/Clarion-Extension](https://github.com/msarson/Clarion-Extension)** — the server
  lives at `server/src/server.ts` and ships as `out/server/src/server.js` in the released `.vsix`.
- The path referenced by `CLARIONLSP_ROOT` (legacy `H:\DevLaptop\ClarionLSP`) is a **local snapshot**
  of that build output, with our **CodeGraph integration patch** applied on top and the required
  `node_modules` present. It is not a separate private codebase.

Related shared-LSP infrastructure: the add-in prefers a shared `ClarionLsp` add-in at runtime when
present (via `SharedLspBridge` / `ClarionLspLocator`) and falls back to this bundled server otherwise
— see issues [#17](https://github.com/ClarionLive/ClarionAssistant/issues/17) (closed) and
[msarson/clarion-lsp](https://github.com/msarson/clarion-lsp).

> **Drift (#40):** because `CLARIONLSP_ROOT` is a hand-maintained snapshot, it can drift from upstream.
> The planned direction is to pull a **pinned, tagged** `server.js` from `msarson/Clarion-Extension`
> and apply the CodeGraph patch at build time instead of copying a local snapshot. Until that lands,
> keep the snapshot reasonably current and coordinate tag cadence with upstream.

## The clarion-indexer (`indexer/`)

`clarion-indexer.exe` parses a Clarion solution into the CodeGraph SQLite database that the LSP
server's `CodeGraphBridge` queries. It used to build from an external, unpublished tree that pulled
its Graph/Parsing source from a *second* sibling repo (`ClarionCodeGraphAddin`). That made a clean
rebuild impossible. It is now **vendored into this repo, fully self-contained**, under `indexer/`:

- `indexer/Program.cs` — the console entry point.
- `indexer/Graph/`, `indexer/Parsing/` — the **IDE-free** variant of the CodeGraph layer. The
  indexer is a standalone console exe and must **not** take a dependency on `ClarionAssistant.Services`
  (e.g. `RedFileService`). The add-in's own copy under `CodeGraph/` *is* IDE-coupled — these two
  copies are kept deliberately separate. **Do not** re-point `indexer/ClarionIndexer.csproj` at
  `..\CodeGraph\` or the standalone build breaks.

Build it directly if needed:

```powershell
msbuild indexer\ClarionIndexer.csproj /p:Configuration=Debug /p:Platform=x86
```

The SQLite reference resolves from the NuGet global-packages folder portably
(`$(USERPROFILE)\.nuget\packages\...`), so there's no per-machine path to edit.

## better-sqlite3 ABI (#42)

The LSP server's CodeGraph bridge uses **better-sqlite3**, a native Node addon compiled for a
specific Node ABI/architecture. If the bundled `node.exe` drifts from the Node version the prebuilt
addon was compiled against, `require('better-sqlite3')` throws at runtime and `CodeGraphBridge`
**silently self-disables** on end-user installs.

To prevent silent regressions, `deploy.ps1` runs a **build-time ABI assertion**: after copying
`node.exe` and `better-sqlite3` into the add-in folder, it invokes the *deployed* `node.exe` to
`require('better-sqlite3')` and open an in-memory database (the exact end-user load path). If it
fails, the deploy reports `FAIL  better-sqlite3 ABI mismatch (GitHub #42)` and a non-zero failure
count. If you hit this, rebuild/replace `better-sqlite3` against the bundled node's ABI/arch.

## Line endings (#34)

Source files use **CRLF** and are **not** BOM-encoded. Keep your editor configured accordingly so
diffs stay clean.

## Installer

The end-user installer (Inno Setup `.iss`) lives in the separate installer repository, not here. It
has its own machine-path hard-codes that are tracked/addressed there; this repo's `deploy.ps1` is the
developer build/deploy path.

## Pull requests

- Keep changes focused; reference the relevant issue number.
- If you touch `deploy.ps1`, the indexer, or the LSP bundling, verify a clean
  `.\deploy.ps1 -Version 12` (with the Clarion IDE closed) still succeeds end-to-end, including the
  better-sqlite3 ABI assertion.
