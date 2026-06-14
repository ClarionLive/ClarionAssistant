# Spec (HOLD): feasibility spike — tap the IDE's native Clarion completion engine

Status: **HOLD until John confirms Bob's current LSP local-var completion fix works.** Do not start
coding until then. This is a strategic alternative for the Modern/Monaco embeditor path only.
Owner: Bob. Author: CA-Terminal-1-CC.

## Why
The native Clarion editor already resolves `str StringTheory` → `str.` → StringTheory members. That
intelligence is an in-process Clarion parser we can call directly, giving editor-IDENTICAL completion
without reimplementing local-var typing / include resolution / member lookup in the TS LSP server.
The Modern/Monaco embeditor completion already flows through `SharedLspBridge` (in-process C#) before
the out-of-process Node LSP — so an in-process call into the native engine can serve completion there
with zero Node round-trip and zero reimplementation. Parallels the RedirectionFile discovery
(see docs/IdeRedirectionService-spec.md). Knowledge entries #45 (RedirectionFile) and #46 (this engine).

## The engine (verified via reflection; in-process assembly `ClarionParser v12`)
DLL: C:\Clarion12\bin\Addins\BackendBindings\ClarionBinding\Common\ClarionParser.dll
Namespace: `SoftVelocity.Common.Parser` (full parser: CLexAnalyzer, SyntaxAnalyzer.CAnalyzer,
PreProccesor for includes/macros; AST in `SoftVelocity.Common.Parser.Ast.*`).
IDE-intelligence entry points in `SoftVelocity.Common.Parser.IDE.*`:
- `ClarionParser` (static): `ProjectUnit CreatePU(CompilerOptions)`, `ParseFiles(CompilerOptions)`,
  `ParseFile(CompilerOptions)`, `Expression ParseExpression(CompilerOptions, string)`,
  `MethodDefinition ParseMethodCode(CompilerOptions, string)`, `int CheckQuery(CompilerOptions, fileName, fileContent, ref endLine, ref endColumn)`.
- `ClaMemberLookupHelper` (static): `FindOverload`, `RankOverloads`, `InferTypeArgument`,
  `ClassType2ClarionType`, `ConversionExists`, `GetBetterConversion`, `GetCommonType`,
  `GetTypeParameterPassedToBaseClass`. Common signature shape:
  `(CompilerOptions options, string fileName, string fileContent, ref int endLine, ref int endColumn)`
  → hand it BUFFER TEXT + CARET position. This is the member/overload resolution surface.
- `ClaLocalVariablesVisitor`: `AddVariable(TypeReference, name, startPos, endPos, isConst)` + visits
  Block/For/Foreach/etc → enumerates in-scope locals (handles `str StringTheory` value instances and
  `&Ref` references).
- `ClaTypeVisitor`: VisitFieldReferenceExpression (the `x.member` node), VisitInvocationExpression,
  VisitIdentifierExpression → expression type inference.
- `IEnvironmentInformationProvider` / `ClaRefactoryInformationProvider`: `bool HasField(fullTypeName, fieldName)`
  — the environment hook the IDE implements. `CompilerOptions` carries the search-path/redirection env.

NOTE: reflection-ONLY load can't resolve these param types (needs ICSharpCode.SharpDevelop.Dom v2.1.0.2447
preloaded). IN-PROCESS that dep is already loaded, so normal reflection (Type.GetType + MethodInfo.Invoke)
will bind fine from inside the addin.

## The spike (smallest thing that proves/disproves the approach)
Goal: from inside the ClarionAssistant addin (in-process), get the native engine to return StringTheory's
members for a known buffer+caret. Use the REAL in-situ case:
  File: C:\Clarion12\Accessory\Libsrc\win\Reflection.Clw
  Method ReflectClass.SetAttributes — local `str  StringTheory` (line 130), `str.SetValue(...)` (line 134).
  Caret: immediately after `str.` on line 134.

Steps:
1. Discover the shape of `CompilerOptions` (`SoftVelocity.Common.Parser.IDE.CompilerOptions`) — reflect its
   public ctor/props IN-PROCESS (params resolve there). It almost certainly needs: include/search paths
   (feed from the live RedirectionFile / get_solution_info incSearchPaths), the clarion version/target, and
   possibly an IEnvironmentInformationProvider. Find how the IDE itself builds it — search loaded types for
   who calls CreatePU/ClaMemberLookupHelper (the native editor's completion provider). `inspect_ide` +
   reflection over CWBinding.ClarionEditor / the embeditor completion provider should reveal the real call site.
2. Build a CompilerOptions for the active solution (search paths from RedirectionFile.EvaluatedPaths /
   IdeRedirectionService once that exists; until then, get_solution_info.incSearchPaths).
3. Call the parser to get a ProjectUnit / parse the buffer, then invoke the member-lookup path at the caret
   (ClaMemberLookupHelper.* or walk ClaTypeVisitor on the FieldReferenceExpression `str.` to get the type,
   then enumerate members of StringTheory). Exact method TBD from step 1's call-site reverse-engineering.
4. SUCCESS = the call returns StringTheory members (SetValue, Split, GetValue, MakeGuid, Trim, ...).
   Log the list. That single result proves the entire engine-tap approach.

## If the spike succeeds — productionization sketch (do NOT build in the spike)
- New in-process `Services/NativeCompletionService.cs` wrapping the engine (reflection-cached MethodInfos).
- Hook it into `SharedLspBridge.GetCompletion(...)` as a PREFERRED in-process provider for the Modern
  embeditor: try native-engine completion first; fall back to the existing LSP path on null/throw.
- Feed search-path env from IdeRedirectionService (RedirectionFile) so it matches the compiler exactly.
- Keep the LSP path as the provider for the NATIVE editor and any external LSP clients (the engine-tap is
  in-process only; it does not replace the LSP, it augments the Monaco path).

## Reverse-engineering aid (do this first in the spike)
Find the native editor's OWN completion call site — it's the ground truth for how to call the engine:
- Reflect CWBinding.dll IN-PROCESS (reflection-only failed on deps) for the editor's completion provider
  (an ICompletionDataProvider impl — see knowledge: "Clarion-customized ICSharpCode completion API").
- Whatever it passes to ClaMemberLookupHelper / how it builds CompilerOptions IS the recipe. Mirror it.

## Risks / open questions
- CompilerOptions construction is the main unknown (env wiring). Step 1 de-risks it.
- Threading: completion may need to run on/with the IDE's parse state; verify thread-safety of static calls.
- Perf: ParseFiles over a big unit could be slow; the native editor caches a ProjectUnit — find and reuse it
  rather than re-parsing per keystroke.
- This is an UNDOCUMENTED internal API and can change between Clarion versions — wrap defensively, feature-flag,
  and fall back to the LSP path on any failure.
