using System;
using System.Collections.Generic;
using System.IO;
using ClarionAssistant.Services;

// Harness for GH #204 (ticket 708139c0): build_app / generate_source / build_solution decide whether a project is
// .app-driven or hand-written source, and report where its binary really goes, by reading the .cwproj / .sln / .red.
//
// THE BUG. The tools assumed every project is .app-based, so generate_source on a hand-written project ran ClarionCL
// into "Cannot create an application because no templates have been registered", which reads like a build failure.
// And nothing said where the .exe/.dll lands, so callers looked in a conventional folder, did not find it, and
// called a good build a failure. They also said "BUILD SUCCEEDED" for ClarionCL /ag, which never compiles.
//
//   K  project kind: Generated Compile items + sibling .app = App; .sln Solution Items .app = App; generated items
//      with no .app = AppMissing; neither = Source. Refusals say "nothing to generate" and give the MSBuild line.
//   O  output: <OutputPath> (unconditional and per-configuration, both condition shapes), else the configuration's
//      .red sections (first matching entry, [Debug32] before [Common], a project-local version-named .red wins),
//      else the project folder; extension from OutputType; exists + last write time, never worded as fresh.
//   H  the ClarionCL headline never says BUILD SUCCEEDED.
//   W  the registry actually routes the three tools through this (source scan of McpToolRegistry.cs).
//
// Fixtures are the shapes of real projects: an IDE-generated app project (PRM002.cwproj beside PRM002.app) and a
// hand-written one (prg4.cwproj), trimmed. Written to %TEMP% per run.
//
// Run:  tests\Run-Tests.ps1     (args: the ClarionAssistant project dir, for the W source scan)
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class ClarionBuildTargetTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text);
    }

    static bool Has(string s, string part) { return s != null && s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0; }
    static bool Same(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

    const string Head = "<Project DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n";
    const string Defaults =
          "    <Configuration Condition=\" '$(Configuration)' == '' \">Debug</Configuration>\r\n"
        + "    <Platform Condition=\" '$(Platform)' == '' \">Win32</Platform>\r\n";
    const string Tail = "  <Import Project=\"$(ClarionBinPath)\\SoftVelocity.Build.Clarion.targets\" />\r\n</Project>\r\n";

    /// <summary>A .cwproj: <paramref name="props"/> in the first PropertyGroup, <paramref name="extraGroups"/> after it.</summary>
    static string Cwproj(string name, string outputType, string props, string extraGroups, bool generated)
    {
        string meta = generated ? ">\r\n      <Generated>true</Generated>\r\n    </Compile>" : " />";
        return Head
            + "  <PropertyGroup>\r\n    <ProjectGuid>{BD67D7F6-B6E9-4D6B-9CA5-B6DDB123C531}</ProjectGuid>\r\n" + Defaults
            + "    <OutputType>" + outputType + "</OutputType>\r\n    <AssemblyName>" + name + "</AssemblyName>\r\n"
            + "    <OutputName>" + name + "</OutputName>\r\n" + (props ?? "") + "  </PropertyGroup>\r\n"
            + "  <PropertyGroup Condition=\" '$(Configuration)' == 'Debug' \">\r\n    <DebugSymbols>True</DebugSymbols>\r\n  </PropertyGroup>\r\n"
            + (extraGroups ?? "")
            + "  <ItemGroup>\r\n    <FileDriver Include=\"TOPSPEED\" />\r\n"
            + "    <Compile Include=\"" + name + ".clw\"" + meta + "\r\n"
            + "    <Compile Include=\"" + name + "001.clw\"" + meta + "\r\n"
            + "  </ItemGroup>\r\n" + Tail;
    }

    static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-build-target-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The solution-level .red, version-named (the project-local override must use the same name).
            string slnDir = Path.Combine(root, "Sln");
            string red = Path.Combine(slnDir, "Clarion120.red");
            Write(red, "[Debug32]\r\n*.obj = .\\Obj\r\n*.dll = .\\bin\r\n*.lib = .\\lib\r\n"
                     + "[Release32]\r\n*.dll = .\\relbin\r\n"
                     + "[Common]\r\n*.clw = .\\Source\r\nPRMX1.exe = %ROOT%\\special\r\n*.exe = ..\\exebin\r\n*.dll = .\r\n");
            var macros = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "ROOT", "C:\\Clarion12" } };
            var svc = new RedFileService();
            Ok("the fixture .red loads", svc.Load(red, macros));

            // ---- K: app project (IDE shape: Generated items, sibling .app) ----
            string appDir = Path.Combine(slnDir, "v61PRM002");
            string appProj = Path.Combine(appDir, "PRM002.cwproj");
            Write(appProj, Cwproj("PRM002", "Library", "    <Model>Dll</Model>\r\n", null, true));
            Write(Path.Combine(appDir, "PRM002.app"), "app");
            var app = ClarionBuildTarget.Inspect(appProj, "Debug", svc, null);
            Ok("K app project: kind App", app.Error == null && app.Kind == ClarionProjectKind.App, app.Error ?? app.Kind.ToString());
            Ok("K ...its .app is the sibling", Same(app.AppPath, Path.Combine(appDir, "PRM002.app")), app.AppPath);
            Ok("K ...evidence names the generated items and the sibling",
               app.GeneratedCount == 2 && app.CompileCount == 2 && Has(string.Join("|", app.Evidence.ToArray()), "PRM002.app beside"),
               string.Join("|", app.Evidence.ToArray()));
            Ok("K ...no refusal for an app project", ClarionBuildTarget.Refusal("generate_source", app, "msbuild.exe", "C:\\Clarion12") == null);

            // ---- K: source project (prg4 shape: plain Compile items, no .app) ----
            string srcDir = Path.Combine(slnDir, "prg4");
            string srcProj = Path.Combine(srcDir, "prg4.cwproj");
            Write(srcProj, Cwproj("prg4", "WinExe", "    <Model>Lib</Model>\r\n", null, false));
            var src = ClarionBuildTarget.Inspect(srcProj, "Debug", svc, new List<string>());
            Ok("K source project: kind Source", src.Error == null && src.Kind == ClarionProjectKind.Source, src.Error ?? src.Kind.ToString());
            string refuse = ClarionBuildTarget.Refusal("generate_source", src, "C:\\VS\\MSBuild.exe", "C:\\Clarion12");
            Ok("K ...generate_source is refused: 'prg4 is a source project with no .app; nothing to generate'",
               Has(refuse, "prg4 is a source project with no .app; nothing to generate"), refuse);
            Ok("K ...the refusal hands the MSBuild line for the .cwproj and configuration",
               Has(refuse, "\"C:\\VS\\MSBuild.exe\" \"" + srcProj + "\"") && Has(refuse, "/p:ClarionBinPath=\"C:\\Clarion12\\bin\"")
               && Has(refuse, "/p:Configuration=Debug"), refuse);
            Ok("K ...and still reports the project's OUTPUT", Has(refuse, "OUTPUT: " + Path.Combine(Path.GetFullPath(Path.Combine(srcDir, "..\\exebin")), "prg4.exe")), refuse);
            Ok("K ...build_app is refused the same way",
               Has(ClarionBuildTarget.Refusal("build_app", src, null, null), "nothing to generate"));

            // ---- K: generated items but the .app is gone ----
            string lostDir = Path.Combine(slnDir, "Lost");
            string lostProj = Path.Combine(lostDir, "LOST.cwproj");
            Write(lostProj, Cwproj("LOST", "Library", null, null, true));
            var lost = ClarionBuildTarget.Inspect(lostProj, "Debug", svc, null);
            Ok("K generated items, no .app: kind AppMissing", lost.Kind == ClarionProjectKind.AppMissing, lost.Kind.ToString());
            Ok("K ...refused naming the missing .app", Has(ClarionBuildTarget.Refusal("generate_source", lost, null, null), "no LOST.app was found"));

            // ---- K: the .app is named only by the .sln's Solution Items (elsewhere on disk) ----
            string sln = Path.Combine(slnDir, "Test.sln");
            string farApp = Path.Combine(slnDir, "apps", "FAR.app");
            Write(farApp, "app");
            Write(sln, "\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n"
                     + "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"Solution Items\", \"Solution Items\", \"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\"\r\n"
                     + "\tProjectSection(SolutionItems) = postProject\r\n"
                     + "\t\t.\\v61PRM002\\PRM002.app = .\\v61PRM002\\PRM002.app\r\n"
                     + "\t\t.\\apps\\FAR.app = .\\apps\\FAR.app\r\n"
                     + "\tEndProjectSection\r\nEndProject\r\n"
                     + "Project(\"{12B76EC0-1D7B-4FA7-A7D0-C524288B48A1}\") = \"FAR\", \"far\\FAR.cwproj\", \"{11111111-2222-3333-4444-555555555555}\"\r\nEndProject\r\n");
            var items = ClarionBuildTarget.SolutionItemApps(sln);
            Ok("K SolutionItemApps reads both Solution Items .app paths as full paths",
               items.Count == 2 && Same(items[1], farApp), string.Join(" | ", items.ToArray()));
            string farProj = Path.Combine(slnDir, "far", "FAR.cwproj");
            Write(farProj, Cwproj("FAR", "WinExe", null, null, false));
            var far = ClarionBuildTarget.Inspect(farProj, "Debug", svc, items);
            Ok("K .sln Solution Items alone make it App, with that .app", far.Kind == ClarionProjectKind.App && Same(far.AppPath, farApp), far.Kind + " " + far.AppPath);
            Ok("K ...and without the .sln list it is Source", ClarionBuildTarget.Inspect(farProj, "Debug", svc, null).Kind == ClarionProjectKind.Source);

            // ---- O: .red redirected output ----
            Ok("O Library + [Debug32] *.dll = .\\bin: <project>\\bin\\PRM002.dll",
               Same(app.OutputFile, Path.Combine(appDir, "bin", "PRM002.dll")), app.OutputFile);
            Ok("O ...the result says which .red section decided it", Has(app.OutputDirSource, "[Debug32] *.dll = .\\bin"), app.OutputDirSource);
            var appRel = ClarionBuildTarget.Inspect(appProj, "Release", svc, null);
            Ok("O ...Release takes [Release32]", Same(appRel.OutputFile, Path.Combine(appDir, "relbin", "PRM002.dll")), appRel.OutputFile);
            var appOther = ClarionBuildTarget.Inspect(appProj, "Profile", svc, null);
            Ok("O ...a configuration with no section falls to [Common] (*.dll = .)", Same(appOther.OutputFile, Path.Combine(appDir, "PRM002.dll")), appOther.OutputFile);
            Ok("O WinExe + [Common] *.exe = ..\\exebin, relative to the PROJECT folder",
               Same(src.OutputFile, Path.Combine(slnDir, "exebin", "prg4.exe")), src.OutputFile);
            string prmxDir = Path.Combine(slnDir, "prmx");
            Write(Path.Combine(prmxDir, "PRMX1.cwproj"), Cwproj("PRMX1", "Exe", null, null, false));
            var prmx = ClarionBuildTarget.Inspect(Path.Combine(prmxDir, "PRMX1.cwproj"), "Debug", svc, null);
            Ok("O the FIRST matching entry wins (exact PRMX1.exe before *.exe), macros expanded",
               Same(prmx.OutputFile, "C:\\Clarion12\\special\\PRMX1.exe"), prmx.OutputFile);

            // A project-local version-named .red supersedes the solution's.
            string localDir = Path.Combine(slnDir, "local");
            Write(Path.Combine(localDir, "LOCAL.cwproj"), Cwproj("LOCAL", "Exe", null, null, false));
            Write(Path.Combine(localDir, "Clarion120.red"), "[Common]\r\n*.exe = .\\localbin\r\n");
            var local = ClarionBuildTarget.Inspect(Path.Combine(localDir, "LOCAL.cwproj"), "Debug", svc, null);
            Ok("O a project-local Clarion120.red supersedes the solution .red", Same(local.OutputFile, Path.Combine(localDir, "localbin", "LOCAL.exe")), local.OutputFile + " / " + local.OutputDirSource);

            // ---- O: <OutputPath> in the project ----
            string outDir = Path.Combine(slnDir, "outp");
            Write(Path.Combine(outDir, "OUTP.cwproj"), Cwproj("OUTP", "WinExe", "    <OutputPath>Output</OutputPath>\r\n", null, false));
            var outp = ClarionBuildTarget.Inspect(Path.Combine(outDir, "OUTP.cwproj"), "Debug", svc, null);
            Ok("O an unconditional <OutputPath>Output</OutputPath> beats the .red", Same(outp.OutputFile, Path.Combine(outDir, "Output", "OUTP.exe")), outp.OutputFile);
            Ok("O ...and says so", Has(outp.OutputDirSource, "<OutputPath>"), outp.OutputDirSource);

            string cfgDir = Path.Combine(slnDir, "percfg");
            Write(Path.Combine(cfgDir, "PERCFG.cwproj"), Cwproj("PERCFG", "WinExe", null,
                  "  <PropertyGroup Condition=\" '$(Configuration)|$(Platform)' == 'Release|Win32' \">\r\n    <OutputPath>R\\out</OutputPath>\r\n  </PropertyGroup>\r\n"
                + "  <PropertyGroup Condition=\" '$(Configuration)' == 'Debug' \">\r\n    <OutputPath>H:\\Abs\\dbg</OutputPath>\r\n  </PropertyGroup>\r\n", false));
            var pcRel = ClarionBuildTarget.Inspect(Path.Combine(cfgDir, "PERCFG.cwproj"), "Release", svc, null);
            var pcDbg = ClarionBuildTarget.Inspect(Path.Combine(cfgDir, "PERCFG.cwproj"), "Debug", svc, null);
            var pcOther = ClarionBuildTarget.Inspect(Path.Combine(cfgDir, "PERCFG.cwproj"), "Profile", svc, null);
            Ok("O per-configuration OutputPath: Release ('Configuration|Platform' form)", Same(pcRel.OutputFile, Path.Combine(cfgDir, "R", "out", "PERCFG.exe")), pcRel.OutputFile);
            Ok("O ...Debug (absolute)", Same(pcDbg.OutputFile, "H:\\Abs\\dbg\\PERCFG.exe"), pcDbg.OutputFile);
            Ok("O ...a configuration without one falls back to the .red", Same(pcOther.OutputFile, Path.Combine(slnDir, "exebin", "PERCFG.exe")), pcOther.OutputFile);

            // Unknown configuration: the project's own default (Debug) applies, and that is noted.
            var pcUnknown = ClarionBuildTarget.Inspect(Path.Combine(cfgDir, "PERCFG.cwproj"), null, svc, null);
            Ok("O unknown configuration: the .cwproj's default (Debug) is used", pcUnknown.Configuration == "Debug" && Same(pcUnknown.OutputFile, pcDbg.OutputFile), pcUnknown.Configuration + " " + pcUnknown.OutputFile);
            Ok("O ...with a note saying it was assumed", Has(pcUnknown.ConfigurationNote, "not known"), pcUnknown.ConfigurationNote);

            // No .red and no OutputPath: the project folder.
            var noRed = ClarionBuildTarget.Inspect(srcProj, "Debug", null, null);
            Ok("O no .red, no OutputPath: the project folder", Same(noRed.OutputFile, Path.Combine(srcDir, "prg4.exe")), noRed.OutputFile);

            // ---- O: exists / timestamp, never worded as fresh ----
            Ok("O output absent: reported as not there", !app.OutputExists && Has(ClarionBuildTarget.Describe(app), "no such file yet"), ClarionBuildTarget.Describe(app));
            Write(app.OutputFile, "MZ");
            var when = new DateTime(2026, 1, 2, 3, 4, 5);
            File.SetLastWriteTime(app.OutputFile, when);
            string described = ClarionBuildTarget.Describe(app);   // re-reads the file, as the tools do after ClarionCL ends
            Ok("O output present: exists, with its last write time", app.OutputExists && Has(described, "2026-01-02 03:04:05"), described);
            Ok("O ...worded as existing output, not produced by this call", Has(described, "existing output, not produced by this call"), described);
            Ok("O ...and the PROJECT line carries kind + evidence", Has(described, "PROJECT: PRM002.cwproj - app-generated from") && Has(described, "Generated>true"), described);

            // ---- G: .red patterns ----
            // ---- G: the output lookup matches .red entries exactly as the resolver does (one matcher) ----
            string starDir = Path.Combine(slnDir, "star");
            Write(Path.Combine(starDir, "Clarion120.red"), "[Common]\r\n*.* = .\\allout\r\n*.exe = .\\later\r\n");
            Write(Path.Combine(starDir, "STAR.cwproj"), Cwproj("STAR", "Exe", null, null, false));
            var star = ClarionBuildTarget.Inspect(Path.Combine(starDir, "STAR.cwproj"), "Debug", svc, null);
            Ok("G *.* is a catch-all that wins when it comes first", Same(star.OutputFile, Path.Combine(starDir, "allout", "STAR.exe")), star.OutputFile);
            string modDir = Path.Combine(slnDir, "star2");
            Write(Path.Combine(modDir, "Clarion120.red"), "[Common]\r\n*.* = .\\allout\r\n");
            Write(Path.Combine(modDir, "MODX.cwproj"), Cwproj("MODX", "Module", null, null, false));
            var modx = ClarionBuildTarget.Inspect(Path.Combine(modDir, "MODX.cwproj"), "Debug", svc, null);
            Ok("G *.* matches even a name with no dot (unknown OutputType), as RedFileService does",
               Same(modx.OutputFile, Path.Combine(modDir, "allout", "MODX")), modx.OutputFile);
            Write(modx.OutputFile, "x");
            var modRed = RedFileService.ForProjectDirectory(modDir, svc);
            Ok("G ...and the resolver finds the file at exactly that path",
               Same(modRed.ResolveFrom("MODX", modDir, "Common"), modx.OutputFile), modRed.ResolveFrom("MODX", modDir, "Common"));
            Write(star.OutputFile, "MZ");
            var starRed = RedFileService.ForProjectDirectory(starDir, svc);
            Ok("G ...same for STAR.exe", Same(starRed.ResolveFrom("STAR.exe", starDir, "Common"), star.OutputFile), starRed.ResolveFrom("STAR.exe", starDir, "Common"));
            Ok("G extensions: Exe/WinExe .exe, Library .dll, Lib .lib, other null",
               ClarionBuildTarget.OutputExtension("WinExe") == ".exe" && ClarionBuildTarget.OutputExtension("Exe") == ".exe"
               && ClarionBuildTarget.OutputExtension("Library") == ".dll" && ClarionBuildTarget.OutputExtension("Lib") == ".lib"
               && ClarionBuildTarget.OutputExtension("Module") == null);

            // ---- H ----
            Ok("H the ClarionCL headline never claims a build",
               !Has(ClarionBuildTarget.GenerationHeadline(true), "BUILD") && !Has(ClarionBuildTarget.GenerationHeadline(false), "BUILD"));
            Ok("H ...and says outright nothing was compiled, success or failure",
               Has(ClarionBuildTarget.GenerationHeadline(true), "GENERATION SUCCEEDED — NOT COMPILED: no .exe/.dll was produced by this call")
               && Has(ClarionBuildTarget.GenerationHeadline(false), "GENERATION FAILED — NOT COMPILED: no .exe/.dll was produced by this call"),
               ClarionBuildTarget.GenerationHeadline(true));
            Ok("H every PROJECT line (build_solution's per-project lines too) says NOT COMPILED by this call",
               Has(ClarionBuildTarget.Describe(app), "NOT COMPILED by this call") && Has(ClarionBuildTarget.Describe(src), "NOT COMPILED by this call"));
            Ok("H the note says generated, not compiled", Has(ClarionBuildTarget.GenerateOnlyNote, "generated, not compiled"));

            // ---- W: the registry routes the three tools through this ----
            string projectDir = args.Length > 0 ? args[0] : null;
            string registry = projectDir != null ? Path.Combine(projectDir, "Services", "McpToolRegistry.cs") : null;
            if (registry == null || !File.Exists(registry))
                Ok("W McpToolRegistry.cs found (pass the ClarionAssistant dir)", false, registry ?? "(no arg)");
            else
            {
                string reg = File.ReadAllText(registry);
                Ok("W build_app refuses through ClarionClRefusal", Has(reg, "ClarionClRefusal(\"build_app\", target)"));
                Ok("W generate_source refuses through ClarionClRefusal", Has(reg, "ClarionClRefusal(\"generate_source\", target)"));
                Ok("W build_app / generate_source report the project after ClarionCL ends", Count(reg, "resultPreamble: () => target.Describe()") == 2);
                Ok("W build_solution reports every project", Has(reg, "resultPreamble: describeAll"));
                Ok("W ClarionCL tools get the generation headline", Has(reg, "generateOnly ? ClarionBuildTarget.GenerationHeadline(success)"));
            }
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("  [FAIL] harness threw: " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("ClarionBuildTarget.Test: " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    static int Count(string s, string part)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(part, i, StringComparison.Ordinal)) >= 0) { n++; i += part.Length; }
        return n;
    }
}
