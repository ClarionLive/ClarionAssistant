# Handoff spec: IdeRedirectionService + red_trace (in-process RED resolution)

Owner: **Bob** (coder). Author: CA-Terminal-1-CC (research/design).
Goal: stop rolling our own RED parser; defer file/path resolution to the IDE's live,
config-aware redirection engine (`Clarion.Core.Redirection.RedirectionFile`), which is the
absolute source of truth (dev can change build config / RED at will).

## Background (verified)
- The IDE holds a live instance of `Clarion.Core.Redirection.RedirectionFile` (in `Clarion.Core.dll`,
  already loaded in the IDE AppDomain). `Clarion.prj.dll` holds a `gcroot<RedirectionFile^>`.
- It is config-aware (ActiveSection/GlobalActiveSection), expands all macros, parses nested
  `{include}` red files, caches (FileListCache), and self-updates on .red/config/version change.
- Our `Services/RedFileService.cs` is a strictly inferior reimplementation (Common-only, hardcoded
  %BIN%/%ROOT%/%REDDIR%, ignores `{include}`, no read-vs-write semantics). Keep it ONLY as a headless
  fallback for out-of-process callers that can't reach Clarion.Core.

## Verified public API of RedirectionFile (reflection)
Static:
- `RedirectionFile GetActiveRedirectionFile()`
- `RedirectionFile GetActiveRedirectionFile(bool forWindows)`
- `RedirectionFile GetRedirectionFile(bool forWin, string version)`
- `RedirectionFile GetRedirectionFile(string directory, string version, bool forWin, bool useDefault)`
- `RedirectionFile Create(string directory[, bool forWin | string version])`
- `void Initialize(string configDir)`
- props: static `string GlobalActiveSection`, static `bool ThrowErrorOnLoadFailure`, static `string CurrentDirectory`

Instance (root = project/app directory; anchors relative redirection entries):
- `string OpenName(string fileName, string root)` — resolve for READING → full compiler-accurate path
- `List<string> OpenNames(string fileName, string root)` — ALL matches in search order
- `string CreateName(string fileName, string root)` — resolve WRITE/generated-output location
- `Dictionary<?,?> EvaluatedPaths(string pattern, string root)` — search dirs for a pattern e.g. "*.inc"
- `bool Exists(string fileName, string root)`
- `List<string> Trace(string fileName, string root)` — ordered dir-by-dir search trace
- `void ActivateSection(string)` / `void DeactivateSection(string)`
- props: `string ActiveSection`, `string FullName` (the active .red path)

NOTE: no public change EVENT (watchers are internal nested classes w/ only Detach()). For re-push,
hook the IDE's own config/solution/version-change events + a FileSystemWatcher on `FullName`.

## Deliverable 1 — Services/IdeRedirectionService.cs (reflection wrapper)
A thin, defensively-wrapped wrapper. Cache MethodInfos. On any reflection failure or null active
instance, fall back to `RedFileService`. Suggested surface:

```csharp
public static class IdeRedirectionService
{
    public static bool IsAvailable { get; }            // Clarion.Core type present + GetActiveRedirectionFile() != null
    public static string ActiveRedPath { get; }        // FullName
    public static string ActiveSection { get; }

    public static string  ResolveOpen(string fileName, string root);        // OpenName
    public static IList<string> ResolveOpenAll(string fileName, string root);// OpenNames
    public static string  ResolveCreate(string fileName, string root);      // CreateName
    public static IList<string> SearchDirs(string pattern, string root);    // EvaluatedPaths keys/values
    public static bool    Exists(string fileName, string root);
    public static IList<string> Trace(string fileName, string root);        // Trace
}
```

Reflection shim (Clarion.Core is in-process, so Type.GetType resolves):
```csharp
var t  = Type.GetType("Clarion.Core.Redirection.RedirectionFile, Clarion.Core");
var rf = t.GetMethod("GetActiveRedirectionFile", Type.EmptyTypes).Invoke(null, null);
var hit = (string)t.GetMethod("OpenName").Invoke(rf, new object[]{ fileName, root });
```
Pass the project/app directory as `root`. Wrap every call in try/catch → fall back to RedFileService.

## Deliverable 2 — MCP tool `red_trace`
Register in `Services/McpToolRegistry.cs`. Args: `filename` (required), optional `root`
(default: active solution dir from get_solution_info). Returns:
- `resolvedOpen` (ResolveOpen), `allMatches` (ResolveOpenAll), `trace` (Trace),
- `activeSection`, `activeRedPath`, `available` (IsAvailable).
Purpose: prove the live engine resolves a file (e.g. StringTheory.inc) before anything depends on it,
and diagnose "why wasn't X found" via the ordered trace.

Optional companion `red_resolve(filename)` if you want a minimal single-path tool.

## Deliverable 3 — repoint existing tools
- `resolve_red_path` → `IdeRedirectionService.ResolveOpen` (fallback RedFileService.Resolve)
- `get_red_search_paths` → `IdeRedirectionService.SearchDirs` (fallback RedFileService.GetSearchPaths)

## Deliverable 4 — feed our bundled server's updatePaths from the live engine
- `Services/LspService.cs` currently builds `clarion/updatePaths` from versionConfig. Make the
  search-dir source authoritative via `IdeRedirectionService.SearchDirs("*.inc"/"*.clw"/..., solutionDir)`.
- `Services/LspClient.cs` sends updatePaths ONCE post-init then nulls it. Add a runtime re-send path
  so config/RED/solution changes can re-push (see change-detection below).
- Out-of-process note: the bundled server.js still can't call RedirectionFile — it consumes pushed dirs.
  This is Model A (push). Model B (server→client resolveFile pull) is v2, only viable where we own both
  ends (our bundled client+server); Mark's shared contract has NO path-push/resolve hook.

## Change detection (for re-push)
No public RedirectionFile event. Hook SharpDevelop ProjectService active-configuration-changed +
solution-loaded, our version-change, and a FileSystemWatcher on `RedirectionFile.FullName`. Debounce →
re-evaluate SearchDirs → re-push updatePaths.

## Cross-ref: the StringTheory.inc bug (server side, Bob)
- StringTheory.inc on disk: `C:\Clarion12\accessory\libsrc\win\StringTheory.inc` (in incSearchPaths).
- ReflectClass (`Reflection.Inc:159`) is `Class()` — NO parent; StringTheory is a PRIVATE MEMBER
  `_worker &StringTheory` (line 164). `include('StringTheory.inc'),ONCE` is at Reflection.Inc:13 (.INC).
- So completion on the member needs: resolve member → `&StringTheory` type → resolve type decl via the
  declaration-file's includes (read StringTheory.inc from accessory\libsrc\win) → expose members.
- Server seam (`resolveIncludePath`) should search the full pushed redirectionPaths+libsrcPaths, not just
  solution-findFile + sibling. When IdeRedirectionService→authoritative-path-push lands, swap the dir
  SOURCE behind that same seam.
