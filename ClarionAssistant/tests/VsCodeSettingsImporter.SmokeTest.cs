using System;
using System.Collections.Generic;
using System.IO;
using ClarionAssistant.Services;

// Smoke test for VsCodeSettingsImporter — compiles the REAL service file (no shims needed; it has
// zero IDE coupling) and exercises the JSONC strip + mapping against a realistic settings.json.
//
// Run:  tests\Run-Tests.ps1          (compiles and runs this; see that script for the raw csc line)
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
// The csproj uses explicit <Compile Include> entries rather than a wildcard, so it stays out on its own.
//
// The whole point of VsCodeSettingsImporter having ZERO IDE coupling is that it can be compiled and
// tested exactly like this, outside Clarion. Keep it that way: if the service ever gains a reference
// to MonacoEditorControl or anything in the IDE object graph, this harness stops compiling and the
// only remaining way to test the JSONC and font-stack logic is a manual click in a running IDE.
static class SmokeTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    static object Val(VsCodeSettingsImporter.Result r, string key)
    {
        object v;
        return r.Values.TryGetValue(key, out v) ? v : null;
    }

    static int Main()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "vscode-smoke-" + Guid.NewGuid().ToString("N") + ".json");

        // A deliberately nasty but entirely legal settings.json:
        //  - // and /* */ comments, including a // INSIDE a string literal
        //  - a trailing comma before }
        //  - a CSS font stack with a quoted family
        //  - the object spelling of editor.minimap
        //  - a [clarion] block that must beat the global tabSize
        //  - an unrecognised wordWrap value that must be reported, not guessed
        string json = String.Join("\n", new string[] {
            "{",
            "  // editor basics",
            "  \"editor.tabSize\": 4,",
            "  \"editor.insertSpaces\": true,",
            "  \"editor.fontSize\": 200,                       /* clamped to 48 */",
            "  \"editor.fontFamily\": \"'Cascadia Code', Consolas, monospace\",",
            "  \"editor.wordWrap\": \"bounded\",",
            "  \"editor.minimap\": { \"enabled\": false },",
            "  \"editor.autoIndent\": \"keep\",",
            "  \"editor.occurrencesHighlight\": \"multiFile\",",
            "  \"editor.scrollbar.horizontal\": \"hidden\",",
            "  \"editor.acceptSuggestionOnCommitCharacter\": false,",
            "  \"files.exclude\": { \"**/.git\": true },",
            "  \"terminal.integrated.cwd\": \"C://tools//bin\",   // a // that is NOT a comment",
            "  \"[clarion]\": {",
            "      \"editor.tabSize\": 2,",
            "  },",
            "}"
        });
        File.WriteAllText(tmp, json);

        Console.WriteLine("VsCodeSettingsImporter smoke test");
        Console.WriteLine();

        var r = VsCodeSettingsImporter.Read(tmp);

        Ok("file found", r.Found, "Found=" + r.Found);
        Ok("no parse error", String.IsNullOrEmpty(r.Error), r.Error);

        Ok("[clarion] tabSize beats global (2, not 4)", Equals(Val(r, "tabSize"), 2), "got " + Val(r, "tabSize"));
        Ok("insertSpaces true", Equals(Val(r, "insertSpaces"), true), "got " + Val(r, "insertSpaces"));
        Ok("fontSize clamped 200 -> 48", Equals(Val(r, "fontSize"), 48), "got " + Val(r, "fontSize"));
        Ok("fontFamily stack -> 'Cascadia Code' unquoted", Equals(Val(r, "fontFamily"), "Cascadia Code"), "got " + Val(r, "fontFamily"));
        Ok("wordWrap 'bounded' -> true", Equals(Val(r, "wordWrap"), true), "got " + Val(r, "wordWrap"));
        Ok("nested editor.minimap.enabled -> false", Equals(Val(r, "minimap"), false), "got " + Val(r, "minimap"));
        Ok("autoIndent 'keep' -> false", Equals(Val(r, "autoIndent"), false), "got " + Val(r, "autoIndent"));
        Ok("occurrencesHighlight 'multiFile' -> true", Equals(Val(r, "occurrenceHighlight"), true), "got " + Val(r, "occurrenceHighlight"));
        Ok("horizontalScrollbar 'hidden'", Equals(Val(r, "horizontalScrollbar"), "hidden"), "got " + Val(r, "horizontalScrollbar"));
        Ok("completeOnInsertKey false", Equals(Val(r, "completeOnInsertKey"), false), "got " + Val(r, "completeOnInsertKey"));

        // The // inside the string literal must not have truncated the file — if it had, the [clarion]
        // block that follows it would be gone and tabSize would be 4.
        Ok("'//' inside a string did not eat the rest of the file", Equals(Val(r, "tabSize"), 2), "tabSize=" + Val(r, "tabSize"));

        // Unmapped Clarion-specific settings must never be proposed.
        Ok("no formatter keys proposed", !r.Values.ContainsKey("preferredColumn") && !r.Values.ContainsKey("keywordCase"), null);
        Ok("no splitOrientation proposed", !r.Values.ContainsKey("splitOrientation"), null);

        Console.WriteLine();
        Console.WriteLine("  values: " + r.Values.Count + "   skipped: " + r.Skipped.Count);
        foreach (var kv in r.Skipped) Console.WriteLine("    skipped " + kv.Key + ": " + kv.Value);

        // ---- direct unit checks on the two helpers ----
        Console.WriteLine();
        Console.WriteLine("Helper checks");
        Ok("StripJsonComments keeps '//' in a string",
            VsCodeSettingsImporter.StripJsonComments("{\"p\":\"a//b\"}").Contains("a//b"), null);
        Ok("StripJsonComments drops a line comment",
            !VsCodeSettingsImporter.StripJsonComments("{\"a\":1 // note\n}").Contains("note"), null);
        Ok("StripJsonComments drops a block comment",
            !VsCodeSettingsImporter.StripJsonComments("{/* gone */\"a\":1}").Contains("gone"), null);
        Ok("StripJsonComments removes a trailing comma in an array",
            VsCodeSettingsImporter.StripJsonComments("[1,2,]").Replace(" ", "") == "[1,2]",
            VsCodeSettingsImporter.StripJsonComments("[1,2,]"));
        Ok("StripJsonComments keeps a comma INSIDE a string",
            VsCodeSettingsImporter.StripJsonComments("{\"a\":\"x,\"}").Contains("x,"), null);
        Ok("FirstFontFamily unquotes a quoted family",
            VsCodeSettingsImporter.FirstFontFamily("'Courier New', monospace") == "Courier New",
            VsCodeSettingsImporter.FirstFontFamily("'Courier New', monospace"));
        Ok("FirstFontFamily on a bare family",
            VsCodeSettingsImporter.FirstFontFamily("Consolas") == "Consolas", null);
        Ok("FirstFontFamily rejects an over-long name (would be blanked by SanitizeFontFamily)",
            VsCodeSettingsImporter.FirstFontFamily(new String('x', 70)) == null, null);
        Ok("FirstFontFamily rejects empty/null",
            VsCodeSettingsImporter.FirstFontFamily("") == null && VsCodeSettingsImporter.FirstFontFamily(null) == null, null);

        // ---- missing + corrupt files ----
        Console.WriteLine();
        Console.WriteLine("Failure modes");
        var missing = VsCodeSettingsImporter.Read(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N") + ".json"));
        Ok("missing file -> Found=false, no error text", !missing.Found && String.IsNullOrEmpty(missing.Error), "Error=" + missing.Error);
        Ok("missing file proposes nothing", missing.Values.Count == 0, null);

        string bad = Path.Combine(Path.GetTempPath(), "vscode-bad-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(bad, "{ this is not json at all ");
        var corrupt = VsCodeSettingsImporter.Read(bad);
        Ok("corrupt file -> Found=true with an error", corrupt.Found && !String.IsNullOrEmpty(corrupt.Error), "Error=" + corrupt.Error);
        Ok("corrupt file proposes nothing", corrupt.Values.Count == 0, null);

        try { File.Delete(tmp); File.Delete(bad); } catch { }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? ("ALL PASS (" + pass + ")") : (fail + " FAILED, " + pass + " passed"));
        return fail == 0 ? 0 : 1;
    }
}
