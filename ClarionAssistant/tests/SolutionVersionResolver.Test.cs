// Harness for Services\SolutionVersionResolver.cs (ticket 0ce0b5e2): which Clarion a solution is, in what order,
// and how the IDE's saved per-solution choice is found without trusting a reproduced string hash.
//
// Compiled by Run-Tests.ps1 against the REAL resolver, IdeSolutionRecord and ClarionVersionService, x86 (the
// IDE's bitness, so the name-hash tiebreak can be checked as the IDE computes it). Fakes only: version lists,
// IDE records, a preferences folder and a solution with .Version resources, all in a temp folder.
//
// What each check stops:
//   order          - a lower tier overriding a higher one (the defect: the host install beat the IDE's choice)
//   pins stop      - an unmatched explicit name silently falling through to another Clarion
//   live vs saved  - a stale preferences file beating the IDE's live choice, including a live 'Current'
//   other solution - the IDE's record for a DIFFERENT solution deciding this one
//   prefs by content / hash / ambiguity - choosing a preferences file by a hash that differs across runtimes
//   .Version       - the advisory mismatch warning firing (or not) on the projects' version resources
//   record         - the addin's record not carrying (or not re-publishing) the version choice
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using ClarionAssistant.Services;

static class SolutionVersionResolverTest
{
    static int _failures, _assertions;

    static void Check(bool ok, string what, string detail = null)
    {
        _assertions++;
        if (!ok) { _failures++; Console.WriteLine("  FAIL " + what + (detail != null ? "  [" + detail + "]" : "")); }
        else Console.WriteLine("  ok   " + what);
    }

    static ClarionVersionConfig V(string name, string root, int cw)
    {
        return new ClarionVersionConfig
        {
            Name = name, RootPath = root, BinPath = Path.Combine(root, "bin"), RedFileName = "x.red",
            IsWindowsVersion = true, CWVersion = cw, LibSrcPaths = new List<string>(), Macros = new Dictionary<string, string>()
        };
    }

    static void WritePrefs(string dir, string file, string activeVersion, string openFile)
    {
        var sb = new StringBuilder();
        sb.Append("<Properties>\r\n  <StartupProject value=\"\" />\r\n");
        sb.Append(openFile == null ? "  <Array name=\"OpenFiles\" />\r\n"
            : "  <Array name=\"OpenFiles\">\r\n    <String value=\"" + openFile + "\" />\r\n  </Array>\r\n");
        if (activeVersion != null) sb.Append("  <ActiveVersion value=\"" + activeVersion + "\" />\r\n");
        sb.Append("</Properties>");
        // The IDE writes its preferences files WITH a UTF-8 BOM, and the reader must cope, so the fixture has one
        // on purpose: preamble + BOM-free bytes, written explicitly (the BOM guard rejects BOM-emitting encoders).
        byte[] bom = Encoding.UTF8.GetPreamble();
        byte[] body = new UTF8Encoding(false).GetBytes(sb.ToString());
        var bytes = new byte[bom.Length + body.Length];
        Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
        Buffer.BlockCopy(body, 0, bytes, bom.Length, body.Length);
        File.WriteAllBytes(Path.Combine(dir, file), bytes);
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-solver-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try
        {
            var c10 = V("Clarion 10 Active And Updated", @"C:\Clarion10v8", 10000);
            var c12 = V("Clarion 12.0.13941", @"C:\Clarion12", 12026);
            var other = V("POSitive v61 C10", @"C:\Clarion10", 10000);
            var all = new List<ClarionVersionConfig> { c10, c12, other };
            Func<string, ClarionVersionConfig> find = n => all.Find(v => string.Equals(v.Name, n, StringComparison.OrdinalIgnoreCase));
            // The host: a C12 install whose "current" is C12, and this server installed under C:\Clarion12.
            var host = new ClarionVersionInfo { Versions = all, CurrentVersionName = c12.Name, HostIsNotIde = true };

            string solDir = Path.Combine(root, "v61"); Directory.CreateDirectory(solDir);
            string sln = Path.Combine(solDir, "v61POSitive.sln");
            File.WriteAllText(sln, "Project(\"{12345678-0000-0000-0000-000000000000}\") = \"PRM002\", \"PRM002\\PRM002.cwproj\", \"{1}\"\r\nEndProject\r\n");
            Directory.CreateDirectory(Path.Combine(solDir, "PRM002"));
            File.WriteAllText(Path.Combine(solDir, "PRM002", "PRM002.Version"),
                "      VALUE \"ClarionVersion\", \"10000\\0\"\r\n      VALUE \"TemplateVersion\", \"v10.0\\0\"\r\n");
            string cfgDir = Path.Combine(root, "idecfg"); string prefs = Path.Combine(cfgDir, "preferences");
            Directory.CreateDirectory(prefs);

            Func<SolutionVersionInputs> baseInputs = () => new SolutionVersionInputs
            {
                SolutionPath = sln, HostInfo = host, HostRoot = @"C:\Clarion12", FindNamed = find
            };
            Func<string, IdeSolutionRecord.Details> live = choice => new IdeSolutionRecord.Details
            {
                Solution = sln, VersionChoice = choice, ConfigDir = cfgDir, Pid = 4242
            };
            SolutionVersionChoice r;

            Console.WriteLine("-- the order");
            r = SolutionVersionResolver.Resolve(baseInputs());
            Check(r.Config == c12 && r.Source == SolutionVersionSource.HostInstall,
                "nothing else: the Clarion tree this server is installed under (C12)", r.Note);

            var i1 = baseInputs(); i1.IdeRecord = live(c10.Name);
            r = SolutionVersionResolver.Resolve(i1);
            Check(r.Config == c10 && r.Source == SolutionVersionSource.IdeLive && r.Note.Contains("live from the IDE (pid 4242)"),
                "THE DEFECT: the IDE's live choice for this solution beats the host install", r.Note);

            var i2 = baseInputs(); i2.IdeRecord = live(c10.Name); i2.PinnedVersion = other.Name;
            r = SolutionVersionResolver.Resolve(i2);
            Check(r.Config == other && r.Source == SolutionVersionSource.SolutionFile, "clarion-assistant.json beats the IDE", r.Note);

            var i3 = baseInputs(); i3.IdeRecord = live(c10.Name); i3.PinnedVersion = other.Name; i3.CommandLineVersion = c12.Name;
            r = SolutionVersionResolver.Resolve(i3);
            Check(r.Config == c12 && r.Source == SolutionVersionSource.CommandLine, "--clarion-version beats everything", r.Note);

            var i4 = baseInputs(); i4.IdeRecord = live(c10.Name); i4.PinnedVersion = "Clarion 99";
            r = SolutionVersionResolver.Resolve(i4);
            Check(r.Config == null && r.Note.Contains("'Clarion 99'"), "an unmatched pin STOPS, it does not fall through", r.Note);

            Console.WriteLine("-- live vs saved");
            WritePrefs(prefs, "v61POSitive.sln.4a0d30a2.xml", other.Name, null);
            var i5 = baseInputs(); i5.IdeRecord = live(c10.Name);
            r = SolutionVersionResolver.Resolve(i5);
            Check(r.Config == c10 && r.Source == SolutionVersionSource.IdeLive, "live beats the saved preferences", r.Note);

            var i6 = baseInputs(); i6.IdeRecord = live(null);
            r = SolutionVersionResolver.Resolve(i6);
            Check(r.Config == other && r.Source == SolutionVersionSource.IdePreferences
                && r.Note.Contains("v61POSitive.sln.4a0d30a2.xml"),
                "no live choice (older addin): the saved preferences in the record's config dir, named", r.Note);

            var i7 = baseInputs(); i7.IdeRecord = live("Current");
            r = SolutionVersionResolver.Resolve(i7);
            Check(r.Config == c12 && r.Source == SolutionVersionSource.HostInstall && r.Note.Contains("'Current'"),
                "a live 'Current' means the IDE's own Clarion (host), NOT the saved preferences", r.Note);

            var i8 = baseInputs(); i8.IdeRecord = live("Clarion 99");
            r = SolutionVersionResolver.Resolve(i8);
            Check(r.Config == other && r.Note.Contains("'Clarion 99'"),
                "a live name nothing defines is reported and falls through to the saved choice", r.Note);

            var i9 = baseInputs(); var rec9 = live(c10.Name); rec9.Solution = Path.Combine(root, "elsewhere.sln"); i9.IdeRecord = rec9;
            r = SolutionVersionResolver.Resolve(i9);
            Check(r.Source == SolutionVersionSource.IdePreferences && r.Config == other,
                "the IDE's live choice for ANOTHER solution does not decide this one", r.Note);

            var i10 = baseInputs(); i10.PreferencesDir = prefs;   // no IDE at all: the host's own preferences folder
            r = SolutionVersionResolver.Resolve(i10);
            Check(r.Source == SolutionVersionSource.IdePreferences && r.Config == other, "no IDE: the host Clarion's preferences folder", r.Note);

            File.Delete(Path.Combine(prefs, "v61POSitive.sln.4a0d30a2.xml"));
            WritePrefs(prefs, "v61POSitive.sln.4a0d30a2.xml", "Current", null);
            r = SolutionVersionResolver.Resolve(i10);
            Check(r.Config == c12 && r.Source == SolutionVersionSource.HostInstall && r.Note.Contains("'Current'"),
                "a saved 'Current' falls to the host and says so", r.Note);
            File.Delete(Path.Combine(prefs, "v61POSitive.sln.4a0d30a2.xml"));

            Console.WriteLine("-- choosing the preferences file");
            string f, note, got;
            WritePrefs(prefs, "v61POSitive.sln.11111111.xml", c10.Name, null);
            WritePrefs(prefs, "v61POSitive.sln.22222222.xml", c10.Name, null);
            got = IdeSolutionPreferences.ReadActiveVersion(prefs, sln, out f, out note);
            Check(got == c10.Name && note != null && note.Contains("all saying the same"), "several files that agree: that choice", note);

            WritePrefs(prefs, "v61POSitive.sln.22222222.xml", c12.Name, Path.Combine(solDir, "PRM002", "PRM002.clw"));
            WritePrefs(prefs, "v61POSitive.sln.4a0d30a2.xml", other.Name, @"C:\Somewhere\Else\x.clw");
            got = IdeSolutionPreferences.ReadActiveVersion(prefs, sln, out f, out note);
            Check(got == c12.Name && Path.GetFileName(f) == "v61POSitive.sln.22222222.xml",
                "files that disagree: the one whose recorded paths are under the solution's folder", note);
            // Add the file the name hash points at, saying something else: content still wins, and says so.
            string hash = IdeSolutionPreferences.HashSuffix(sln);
            WritePrefs(prefs, "v61POSitive.sln." + hash + ".xml", c10.Name, null);
            got = IdeSolutionPreferences.ReadActiveVersion(prefs, sln, out f, out note);
            Check(got == c12.Name && note != null && note.Contains("hash points at v61POSitive.sln." + hash + ".xml"),
                "content and hash disagreeing: content wins and the disagreement is noted", note);

            // No content evidence: the hash breaks the tie (32-bit only, like the IDE).
            foreach (var old in Directory.GetFiles(prefs)) File.Delete(old);
            Check(IntPtr.Size == 4, "harness runs 32-bit, like the IDE (Run-Tests Platform x86)");
            string ours = "v61POSitive.sln." + hash + ".xml";
            WritePrefs(prefs, ours, c10.Name, null);
            WritePrefs(prefs, "v61POSitive.sln.0badf00d.xml", c12.Name, null);
            got = IdeSolutionPreferences.ReadActiveVersion(prefs, sln, out f, out note);
            Check(hash != null && got == c10.Name && Path.GetFileName(f) == ours && note.Contains("hash"),
                "no content evidence: the IDE's name hash breaks the tie", note);

            // The hash the IDE uses really is this one: measured on the Owner's machine, the C12 IDE's preferences
            // file for h:\dev\apositive\v61positive.sln is v61POSitive.sln.4a0d30a2.xml.
            Check(IdeSolutionPreferences.HashSuffix(@"H:\Dev\aPOSitive\v61POSitive.sln") == "4a0d30a2",
                "HashSuffix reproduces the IDE's measured file name", IdeSolutionPreferences.HashSuffix(@"H:\Dev\aPOSitive\v61POSitive.sln"));

            // A short IDE suffix (no leading zeros, like FRAME.sln.a37746e.xml) still matches.
            foreach (var old in Directory.GetFiles(prefs)) File.Delete(old);
            string trimmed = hash.TrimStart('0');
            if (trimmed.Length < hash.Length)
            {
                WritePrefs(prefs, "v61POSitive.sln." + trimmed + ".xml", c10.Name, null);
                WritePrefs(prefs, "v61POSitive.sln.0badf00d.xml", c12.Name, null);
                got = IdeSolutionPreferences.ReadActiveVersion(prefs, sln, out f, out note);
                Check(got == c10.Name, "a suffix written without leading zeros matches", note);
                foreach (var old in Directory.GetFiles(prefs)) File.Delete(old);
            }

            // Disagreeing, no evidence, no hash match: none, and it says why.
            WritePrefs(prefs, "v61POSitive.sln.0badf00d.xml", c10.Name, null);
            WritePrefs(prefs, "v61POSitive.sln.0bad0bad.xml", c12.Name, null);
            got = IdeSolutionPreferences.ReadActiveVersion(prefs, sln, out f, out note);
            Check(got == null && note != null && note.Contains("none was used"), "ambiguous: no answer, and the note lists them", note);
            var i11 = baseInputs(); i11.PreferencesDir = prefs;
            r = SolutionVersionResolver.Resolve(i11);
            Check(r.Source == SolutionVersionSource.HostInstall && r.Note.Contains("none was used"),
                "an ambiguous preferences folder is reported in the final note", r.Note);
            foreach (var old in Directory.GetFiles(prefs)) File.Delete(old);

            // Another solution's file with a similar name is not a candidate.
            WritePrefs(prefs, "v61POSitive.sln.bak.xml", c10.Name, null);
            WritePrefs(prefs, "Xv61POSitive.sln.12345678.xml", c10.Name, null);
            got = IdeSolutionPreferences.ReadActiveVersion(prefs, sln, out f, out note);
            Check(got == null, "only <sln name>.<hex>.xml files are candidates", f);
            foreach (var old in Directory.GetFiles(prefs)) File.Delete(old);

            Console.WriteLine("-- the projects' .Version resources (advisory)");
            string warn = SolutionVersionResolver.ProjectVersionWarning(sln, c12);
            Check(warn != null && warn.Contains("PRM002 (Clarion 10)") && warn.StartsWith("WARNING"), "C12 chosen, project says 10000: warned", warn);
            Check(SolutionVersionResolver.ProjectVersionWarning(sln, c10) == null, "C10 chosen: no warning");
            Check(SolutionVersionResolver.ProjectVersionWarning(sln, null) == null, "no version: no warning");

            Console.WriteLine("-- the addin's record");
            IdeSolutionRecord.RootOverride = Path.Combine(root, "records");
            IdeSolutionRecord.ResetForTest();
            string choice = c10.Name;
            IdeSolutionRecord.VersionChoiceProvider = () => choice;
            IdeSolutionRecord.ConfigDirProvider = () => cfgDir;
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            IdeSolutionRecord.Publish(sln);
            bool transient;
            var d = IdeSolutionRecord.ReadDetails(self, out note, out transient);
            Check(d != null && d.VersionChoice == c10.Name && d.ConfigDir == cfgDir && d.Pid == self,
                "Publish carries the IDE's version choice and config dir", d != null ? d.VersionChoice + " | " + d.ConfigDir : note);
            choice = "Current";
            IdeSolutionRecord.Republish();
            d = IdeSolutionRecord.ReadDetails(self, out note, out transient);
            Check(d != null && d.VersionChoice == "Current" && string.Equals(d.Solution, sln, StringComparison.OrdinalIgnoreCase),
                "a version switch with the same solution open is re-published", d != null ? d.VersionChoice : note);
            IdeSolutionRecord.VersionChoiceProvider = () => { throw new InvalidOperationException("no IDE"); };
            IdeSolutionRecord.Publish(sln);
            d = IdeSolutionRecord.ReadDetails(self, out note, out transient);
            Check(d != null && d.VersionChoice == null && d.Solution != null, "a provider that throws still publishes the solution");
            IdeSolutionRecord.Publish(null);
        }
        finally
        {
            IdeSolutionRecord.VersionChoiceProvider = null;
            IdeSolutionRecord.ConfigDirProvider = null;
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        if (_failures == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + _failures + " of " + _assertions + " assertions");
        return 1;
    }
}
