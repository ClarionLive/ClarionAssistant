// Harness for ticket e1df6c2e (GH #244 items 1 and 2): the standalone server's Clarion SETTINGS FOLDER (a /ConfigDir one
// named by a live IDE, --clarion-config-dir, CLARION_CONFIG_DIR or clarion-assistant.json) and the plugin launcher's
// choice of WHICH installed server to start.
//
// Compiled by Run-Tests.ps1 against the REAL StandaloneConfigDir, SolutionClarionVersion, ServerPicker,
// IdeSolutionRecord and ClarionVersionService. Fakes only: folders, ClarionProperties.xml files, IDE records and
// server candidates, all in a temp folder.
//
// What each check stops:
//   pin            - "configDir" in clarion-assistant.json not read, read unanchored, or a configDir-only file called empty
//   dir order      - a lower tier beating a higher one (json over the command line; anything over a live /ConfigDir IDE)
//   live default   - a live IDE on the DEFAULT folder overriding a named one (that brings back #244's stale copy)
//   fail closed    - a named folder with no ClarionProperties.xml falling through to the default folders
//   config read    - a named version looked up somewhere other than the named folder; settingsFile not reported
//   live scan      - a dead, foreign-solution or unpublished IDE record counted as "a live IDE on this solution"
//   pick order     - the launcher taking a lower tier over a higher one, or the newest when the project said otherwise
//   pick fallback  - the newest-installed fallback not being called a FALLBACK
//   launch note    - serverChosenBy missing, or not saying the running server differs from the pick
using System;
using System.Collections.Generic;
using System.IO;
using ClarionAssistant.McpServer;
using ClarionAssistant.Services;

static class StandaloneConfigDirTest
{
    static int _failures, _assertions;

    static void Check(bool ok, string what, string detail = null)
    {
        _assertions++;
        if (!ok) { _failures++; Console.WriteLine("  FAIL " + what + (detail != null ? "  [" + detail + "]" : "")); }
        else Console.WriteLine("  ok   " + what);
    }

    static bool Has(string s, string part) { return s != null && s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0; }

    static void WriteProps(string dir, string versionName, string root, string probe)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ClarionProperties.xml"),
            "<ClarionProperties>\r\n  <Properties name=\"Clarion.Versions\">\r\n"
            + "    <Properties name=\"" + versionName + "\">\r\n"
            + "      <path value=\"" + root + "\\bin\" />\r\n      <IsWindowsVersion value=\"True\" />\r\n"
            + "      <Properties name=\"RedirectionFile\">\r\n        <Name value=\"Clarion110.red\" />\r\n"
            + "        <Properties name=\"Macros\">\r\n          <root value=\"" + root + "\" />\r\n"
            + "          <probe value=\"" + probe + "\" />\r\n        </Properties>\r\n      </Properties>\r\n"
            + "      <CWVersion value=\"11000\" />\r\n    </Properties>\r\n  </Properties>\r\n</ClarionProperties>\r\n");
    }

    static IdeSolutionRecord.Details Rec(string sln, string configDir, int pid)
    {
        // Details' setters are internal; this harness is compiled into the same assembly.
        return new IdeSolutionRecord.Details { Solution = sln, ConfigDir = configDir, Pid = pid };
    }

    static ServerCandidate Cand(string root, string ver)
    {
        return new ServerCandidate
        {
            Root = root, ExePath = Path.Combine(root, ServerPicker.RelativeExe),
            ClarionExeVersion = ver == null ? null : new Version(ver), Origin = "test"
        };
    }

    static int Main(string[] args)
    {
        Environment.SetEnvironmentVariable("CA_IDE_IMAGE_NAME", System.Diagnostics.Process.GetCurrentProcess().ProcessName);
        string root = Path.Combine(Path.GetTempPath(), "ca-cfgdir-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        try
        {
            string solDir = Path.Combine(root, "sol"); Directory.CreateDirectory(solDir);
            string sln = Path.Combine(solDir, "app.sln");
            File.WriteAllText(sln, "");
            string json = Path.Combine(solDir, SolutionClarionVersion.FileName);

            // ---------------------------------------------------------------- pin
            Console.WriteLine("pin (clarion-assistant.json)");
            File.WriteAllText(json, "{ \"configDir\": \"..\\\\c11.Settings\" }");
            var pin = SolutionClarionVersion.ReadPin(sln);
            Check(string.Equals(pin.ConfigDir, Path.Combine(root, "c11.Settings"), StringComparison.OrdinalIgnoreCase),
                "a relative configDir is anchored at the json's own folder", pin.ConfigDir);
            Check(pin.Version == null && pin.Note == null, "a configDir-only file selects no version and is not called empty", pin.Note);

            Environment.SetEnvironmentVariable("CA_TEST_CFGROOT", root);
            File.WriteAllText(json, "{ \"clarionVersion\": \"Clarion 11.0.13244\", \"configDir\": \"%CA_TEST_CFGROOT%\\\\x\" }");
            pin = SolutionClarionVersion.ReadPin(sln);
            Check(pin.Version == "Clarion 11.0.13244", "clarionVersion still read beside configDir", pin.Version);
            Check(string.Equals(pin.ConfigDir, Path.Combine(root, "x"), StringComparison.OrdinalIgnoreCase), "%VARS% in configDir are expanded", pin.ConfigDir);

            File.WriteAllText(json, "{ \"configDir\": \"%CA_TEST_NO_SUCH_VAR_E1DF%\\\\x\" }");
            pin = SolutionClarionVersion.ReadPin(sln);
            Check(pin.ConfigDir == null && Has(pin.Note, "not a usable path"), "an unexpanded %VAR% is reported, not used as a folder", pin.Note);

            File.WriteAllText(json, "{ \"clarionVersin\": \"typo\" }");
            pin = SolutionClarionVersion.ReadPin(sln);
            Check(Has(pin.Note, "no \"clarionVersion\""), "a file with neither key still says so", pin.Note);
            string legacyNote;
            File.WriteAllText(json, "{ \"clarionVersion\": \"Clarion12\" }");
            Check(SolutionClarionVersion.Read(sln, out legacyNote) == "Clarion12" && legacyNote == null, "Read() keeps its old contract", legacyNote);

            // ---------------------------------------------------------------- dir order
            Console.WriteLine("settings folder order");
            string cA = Path.Combine(root, "cfgA"), cB = Path.Combine(root, "cfgB"), cC = Path.Combine(root, "cfgC"), cLive = Path.Combine(root, "cfgLive");
            string fakeRoot = Path.Combine(root, "Clarion11");
            WriteProps(cA, "CA Test 11", fakeRoot, "fromA");
            WriteProps(cB, "CA Test 11", fakeRoot, "fromB");
            WriteProps(cC, "CA Test 11", fakeRoot, "fromC");
            WriteProps(cLive, "CA Test 11", fakeRoot, "fromLive");
            Func<string, bool> relocated = d => true;              // every temp folder counts as relocated...
            Func<string, bool> notRelocated = d => false;          // ...unless the test says it is the default one
            var pinA = new SolutionClarionVersion.Pin { ConfigDir = cA };

            var ch = StandaloneConfigDir.Resolve(null, null, null, null, root, relocated, null);
            Check(ch.Path == null && ch.Source == StandaloneConfigDir.SourceDefault && ch.Error == null, "nothing named: the default folders", ch.Note);
            Check(Has(ch.Note, StandaloneConfigDir.ArgName) && Has(ch.Note, StandaloneConfigDir.EnvVar), "the default note says how to name one", ch.Note);

            ch = StandaloneConfigDir.Resolve(null, null, null, pinA, root, relocated, null);
            Check(ch.Path == cA && ch.Source == StandaloneConfigDir.SourceSolutionFile, "json configDir alone is used", ch.Path);

            ch = StandaloneConfigDir.Resolve(null, null, cB, pinA, root, relocated, null);
            Check(ch.Path == cB && ch.Source == StandaloneConfigDir.SourceEnvironment && Has(ch.Note, "is ignored"),
                "CLARION_CONFIG_DIR beats the json, and says the json was ignored", ch.Note);

            ch = StandaloneConfigDir.Resolve(null, "cfgC", cB, pinA, root, relocated, null);
            Check(ch.Path == cC && ch.Source == StandaloneConfigDir.SourceCommandLine, "--clarion-config-dir beats both, relative to the working dir", ch.Path);

            var live = new List<IdeSolutionRecord.Details> { Rec(sln, cLive, 4242) };
            ch = StandaloneConfigDir.Resolve(live, cC, null, pinA, root, relocated, null);
            Check(ch.Path == cLive && ch.Source == StandaloneConfigDir.SourceIdeLive, "a live IDE on a relocated folder beats everything", ch.Note);
            Check(Has(ch.Note, "pid 4242") && Has(ch.Note, "IGNORING") && Has(ch.Note, cC), "...and names the pid and what it overrode", ch.Note);

            // ---------------------------------------------------------------- live default
            ch = StandaloneConfigDir.Resolve(live, null, null, pinA, root, notRelocated, null);
            Check(ch.Path == cA && ch.Source == StandaloneConfigDir.SourceSolutionFile, "a live IDE on the DEFAULT folder does not override the json", ch.Note);
            Check(Has(ch.Note, "default settings folder"), "...and the note says the IDE uses the default folder", ch.Note);
            ch = StandaloneConfigDir.Resolve(live, null, null, null, root, notRelocated, null);
            Check(ch.Path == null && ch.Source == StandaloneConfigDir.SourceDefault, "a live IDE on the default folder alone selects no folder", ch.Note);

            // ---------------------------------------------------------------- fail closed
            string missing = Path.Combine(root, "no-such-settings");
            ch = StandaloneConfigDir.Resolve(null, missing, null, pinA, root, relocated, null);
            Check(ch.Error != null && Has(ch.Error, "no ClarionProperties.xml") && Has(ch.Error, "does not exist"),
                "a named folder with no ClarionProperties.xml is an error", ch.Error);
            Check(ch.Path == missing, "...naming the folder it was given, not a fallback", ch.Path);
            string empty = Path.Combine(root, "empty"); Directory.CreateDirectory(empty);
            ch = StandaloneConfigDir.Resolve(null, null, empty, null, root, relocated, null);
            Check(ch.Error != null && !Has(ch.Error, "does not exist"), "an existing folder without the file is an error too", ch.Error);

            // ---------------------------------------------------------------- config read
            Console.WriteLine("reading the named folder");
            var inA = ClarionVersionService.FindNamedInConfigDir(cA, "CA Test 11");
            Check(inA != null && inA.Macros != null && inA.Macros.ContainsKey("probe") && inA.Macros["probe"] == "fromA",
                "the version is read from the named folder's ClarionProperties.xml", inA != null && inA.Macros.ContainsKey("probe") ? inA.Macros["probe"] : "(none)");
            Check(inA != null && string.Equals(inA.SourcePropertiesXml, Path.Combine(cA, "ClarionProperties.xml"), StringComparison.OrdinalIgnoreCase),
                "the entry records which file it came from (settingsFile)", inA != null ? inA.SourcePropertiesXml : null);
            Check(ClarionVersionService.FindNamedInConfigDir(cA, "ca test 11") != null, "names match case-insensitively as a second pass");
            Check(ClarionVersionService.FindNamedInConfigDir(cA, "Clarion 12.0.14000") == null, "a name the folder does not list is not found there");
            var det = ClarionVersionService.DetectInConfigDir(cA, null);
            Check(det != null && det.HostIsNotIde && det.Versions.Count == 1 && det.PropertiesXmlPath == Path.Combine(cA, "ClarionProperties.xml"),
                "DetectInConfigDir reads that folder only", det != null ? det.PropertiesXmlPath : null);
            Check(ClarionVersionService.DetectInConfigDir(empty, null) == null, "DetectInConfigDir of a folder without the file is null (no other folder tried)");
            Check(ClarionConfigDirectory.IsOutsideDefaultRoot(cA), "a temp folder is outside the default settings root");
            string def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SoftVelocity", "Clarion", "11.0");
            Check(!ClarionConfigDirectory.IsOutsideDefaultRoot(def), "%APPDATA%\\SoftVelocity\\Clarion\\11.0 is the default root");

            // ---------------------------------------------------------------- live scan
            Console.WriteLine("live IDE records for the solution");
            string recDir = Path.Combine(root, "records");
            IdeSolutionRecord.RootOverride = recDir;
            IdeSolutionRecord.ConfigDirProvider = () => cLive;
            IdeSolutionRecord.ResetForTest();
            IdeSolutionRecord.Publish(sln);   // this process plays the IDE
            File.WriteAllText(Path.Combine(recDir, "ide-999999.json"),
                "{\"solution\":\"" + sln.Replace("\\", "\\\\") + "\",\"pid\":999999,\"configDir\":\"C:\\\\dead\"}");
            string other = Path.Combine(solDir, "other.sln"); File.WriteAllText(other, "");
            var found = IdeSolutionRecord.FindLiveForSolution(sln);
            Check(found.Count == 1 && found[0].Pid == System.Diagnostics.Process.GetCurrentProcess().Id && found[0].ConfigDir == cLive,
                "only the LIVE IDE's record is found, with its configDir", found.Count + " found");
            Check(IdeSolutionRecord.FindLiveForSolution(other).Count == 0, "an IDE on a different solution is not counted");
            Check(IdeSolutionRecord.FindLiveForSolution(null).Count == 0, "no solution, no records");
            IdeSolutionRecord.Publish(null);
            IdeSolutionRecord.RootOverride = null;

            // ---------------------------------------------------------------- pick order
            Console.WriteLine("launcher server pick");
            string r10 = @"C:\Fake\Clarion10", r11 = @"C:\Fake\Clarion11", r111 = @"C:\Fake\Clarion11.1", r12 = @"C:\Fake\Clarion12";
            var cands = new List<ServerCandidate> { Cand(r10, "10.0.0.12799"), Cand(r11, "11.0.0.13244"), Cand(r111, "11.1.0.13855"), Cand(r12, "12.0.0.14000") };
            var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Clarion 10.0.12799", r10 }, { "Clarion 11.0.13244", r11 }, { "Clarion 11.1.13855", r111 },
                { "Clarion 12.0.14000", r12 }, { "Clarion 9 no server", @"C:\Fake\Clarion9" }
            };
            Func<string, ClarionVersionConfig> find = n =>
            {
                string r;
                return versions.TryGetValue(n, out r) ? new ClarionVersionConfig { Name = n, RootPath = r } : null;
            };
            Func<ServerPickInputs> baseIn = () => new ServerPickInputs { SolutionPath = sln, Candidates = cands, FindNamed = find };

            var pi = baseIn(); pi.PinnedVersion = "Clarion 11.0.13244";
            var pk = ServerPicker.Pick(pi);
            Check(pk.Server != null && pk.Server.Root == r11 && pk.Source == ServerPicker.SourceSolutionFile && !pk.IsFallback,
                "clarion-assistant.json's version picks that Clarion's server", pk.Note);

            pi = baseIn(); pi.PinnedVersion = "Clarion 11.0.13244"; pi.CommandLineVersion = "Clarion 10.0.12799";
            pk = ServerPicker.Pick(pi);
            Check(pk.Server.Root == r10 && pk.Source == ServerPicker.SourceCommandLine, "--clarion-version beats the json", pk.Note);

            pi = baseIn(); pi.PinnedVersion = "Clarion 11.0.13244";
            pi.ProjectMajors.Add(new KeyValuePair<string, int>("A", 10));
            pi.LiveIdes.Add(new LiveIde { Pid = 7, ExePath = r12 + @"\bin\Clarion.exe" });
            pk = ServerPicker.Pick(pi);
            Check(pk.Server.Root == r11, "the json beats the projects' .Version and a live IDE", pk.Note);

            pi = baseIn(); pi.PinnedVersion = "Clarion 9 no server";
            pi.ProjectMajors.Add(new KeyValuePair<string, int>("A", 10));
            pk = ServerPicker.Pick(pi);
            Check(pk.IsFallback && pk.Server.Root == r12 && Has(pk.Note, "not installed there") && Has(pk.Note, "FALLBACK"),
                "a named Clarion without the server STOPS the pick at the reported fallback (no silent other tier)", pk.Note);

            pi = baseIn(); pi.PinnedVersion = "Clarion 13 typo";
            pk = ServerPicker.Pick(pi);
            Check(pk.IsFallback && Has(pk.Note, "matches no Clarion"), "an unknown name is reported, then the fallback", pk.Note);

            pi = baseIn();
            pi.ProjectMajors.Add(new KeyValuePair<string, int>("A", 10));
            pi.ProjectMajors.Add(new KeyValuePair<string, int>("B", 10));
            pk = ServerPicker.Pick(pi);
            Check(pk.Server.Root == r10 && pk.Source == ServerPicker.SourceProjectVersion, "the projects' .Version major picks its Clarion", pk.Note);

            pi = baseIn(); pi.ProjectMajors.Add(new KeyValuePair<string, int>("A", 11));
            pk = ServerPicker.Pick(pi);
            Check(pk.Server.Root == r111 && Has(pk.Note, "2 Clarion 11 installs") && Has(pk.Note, "newest"),
                "two installs of that major: the newest, and the note says so", pk.Note);

            pi = baseIn();
            pi.ProjectMajors.Add(new KeyValuePair<string, int>("A", 10));
            pi.ProjectMajors.Add(new KeyValuePair<string, int>("B", 12));
            pi.LiveIdes.Add(new LiveIde { Pid = 7, VersionChoice = "Current", ExePath = r11 + @"\bin\Clarion.exe" });
            pk = ServerPicker.Pick(pi);
            Check(pk.Server.Root == r11 && pk.Source == ServerPicker.SourceIdeLive && Has(pk.Note, "disagree"),
                "projects that disagree defer to a live IDE ('Current' = its own Clarion)", pk.Note);

            pi = baseIn();
            pi.LiveIdes.Add(new LiveIde { Pid = 8, VersionChoice = "Clarion 10.0.12799", ExePath = r12 + @"\bin\Clarion.exe" });
            pk = ServerPicker.Pick(pi);
            Check(pk.Server.Root == r10 && Has(pk.Note, "Set Clarion Version"), "a live IDE's named Set Clarion Version beats its own exe", pk.Note);

            // ---------------------------------------------------------------- pick fallback
            pk = ServerPicker.Pick(baseIn());
            Check(pk.IsFallback && pk.Source == ServerPicker.SourceNewest && pk.Server.Root == r12, "nothing applies: the newest", pk.Note);
            Check(Has(pk.Note, "FALLBACK") && Has(pk.Note, SolutionClarionVersion.FileName), "...called a FALLBACK, with how to settle it", pk.Note);
            var unknownFirst = new List<ServerCandidate> { Cand(@"C:\Fake\X", null), Cand(r11, "11.0.0.13244") };
            pi = baseIn(); pi.Candidates = unknownFirst;
            Check(ServerPicker.Pick(pi).Server.Root == r11, "an install whose Clarion.exe version is unknown is never 'newest'");
            pi = baseIn(); pi.Candidates = new List<ServerCandidate>();
            pk = ServerPicker.Pick(pi);
            Check(pk.Server == null && pk.Source == ServerPicker.SourceNone, "no server anywhere: no pick", pk.Note);

            // ---------------------------------------------------------------- downgrade guard (pipeline run 1)
            Console.WriteLine("downgrade guard");
            Func<string, string, string, ServerCandidate> CandV = (r, clarion, server) =>
            {
                var c = Cand(r, clarion);
                c.ServerVersion = server == null ? null : new Version(server);
                return c;
            };
            var boot = CandV(r12, "12.0.0.14000", "6.0.0.1400");
            var older11 = CandV(r11, "11.0.0.13244", "6.0.0.1300");
            Func<ServerCandidate, ServerPickInputs> guardIn = c11 => new ServerPickInputs
            {
                SolutionPath = sln, FindNamed = find, Bootstrap = boot,
                Candidates = new List<ServerCandidate> { boot, c11 },
                CapabilityOf = c => ServerPicker.PathPolicyFloor   // at the floor: only the version decides
            };

            // INFERRED choices are refused for being older.
            pi = guardIn(older11); pi.ProjectMajors.Add(new KeyValuePair<string, int>("A", 11));
            pk = ServerPicker.Pick(pi);
            Check(pk.DowngradeRefused && pk.Server.ExePath == boot.ExePath && pk.Warning == null,
                "an INFERRED choice (.Version) of an OLDER server is refused: the bootstrap serves", pk.Note);
            Check(Has(pk.Note, "REFUSED") && Has(pk.Note, "6.0.0.1300") && Has(pk.Note, "6.0.0.1400") && Has(pk.Note, r11),
                "...and the note names both versions and the refused root", pk.Note);

            pi = guardIn(older11); pi.LiveIdes.Add(new LiveIde { Pid = 9, ExePath = r11 + @"\bin\Clarion.exe" });
            Check(ServerPicker.Pick(pi).DowngradeRefused, "an inferred choice from a live IDE is refused too");

            pi = guardIn(CandV(r11, "11.0.0.13244", null)); pi.ProjectMajors.Add(new KeyValuePair<string, int>("A", 11));
            pk = ServerPicker.Pick(pi);
            Check(pk.DowngradeRefused && Has(pk.Note, "version unreadable"), "a server whose version cannot be read is refused too", pk.Note);

            // A version PINNED in clarion-assistant.json is deliberate (pipeline run 2): it runs, with the warning.
            pi = guardIn(older11); pi.PinnedVersion = "Clarion 11.0.13244";
            pk = ServerPicker.Pick(pi);
            Check(!pk.DowngradeRefused && pk.Server.Root == r11 && pk.Source == ServerPicker.SourceSolutionFile,
                "clarion-assistant.json PINS that Clarion: its older server runs", pk.Note);
            Check(Has(pk.Warning, "WARNING") && Has(pk.Warning, "6.0.0.1300") && Has(pk.Warning, "6.0.0.1400")
                  && Has(pk.Warning, SolutionClarionVersion.FileName) && Has(pk.Note, "WARNING"),
                "...with a WARNING naming both versions and the json, in the note too", pk.Warning);

            pi = guardIn(older11); pi.PinnedVersion = "Clarion 11.0.13244"; pi.AllowOlderPinnedServer = false;
            pk = ServerPicker.Pick(pi);
            Check(pk.DowngradeRefused && pk.Server.ExePath == boot.ExePath, "\"allowOlderServer\": false makes the json pin refuse it", pk.Note);

            File.WriteAllText(json, "{ \"clarionVersion\": \"Clarion 11.0.13244\", \"allowOlderServer\": false }");
            var pinAllow = SolutionClarionVersion.ReadPin(sln);
            Check(pinAllow.AllowOlderServer == false && pinAllow.Note == null, "allowOlderServer is read from the json", pinAllow.Note);
            File.WriteAllText(json, "{ \"clarionVersion\": \"Clarion 11.0.13244\", \"allowOlderServer\": \"true\" }");
            pinAllow = SolutionClarionVersion.ReadPin(sln);
            Check(pinAllow.AllowOlderServer == false && Has(pinAllow.Note, "treated as false"),
                "a non-boolean allowOlderServer (even the string \"true\") is treated as FALSE, and said", pinAllow.Note);
            File.WriteAllText(json, "{ \"clarionVersion\": \"Clarion 11.0.13244\" }");
            Check(SolutionClarionVersion.ReadPin(sln).AllowOlderServer == null, "absent allowOlderServer stays null (= allowed)");

            pi = guardIn(CandV(r11, "11.0.0.13244", "6.0.0.1400")); pi.PinnedVersion = "Clarion 11.0.13244";
            pk = ServerPicker.Pick(pi);
            Check(!pk.DowngradeRefused && pk.Server.Root == r11 && pk.Warning == null, "the same version is not a downgrade", pk.Note);

            pi = guardIn(older11); pi.CommandLineVersion = "Clarion 11.0.13244";
            pk = ServerPicker.Pick(pi);
            Check(!pk.DowngradeRefused && pk.Server.Root == r11, "--clarion-version PINS that Clarion: the older server runs", pk.Note);
            Check(Has(pk.Warning, "WARNING") && Has(pk.Warning, "6.0.0.1300") && Has(pk.Warning, "6.0.0.1400") && Has(pk.Note, "WARNING"),
                "...with a WARNING naming both versions, in the note too", pk.Warning);

            // ---------------------------------------------------------------- security floor (pipeline run 3)
            Console.WriteLine("security floor");
            Func<ServerCandidate, ServerPickInputs> floorIn = c11 =>
            {
                var x = guardIn(c11);
                x.CapabilityOf = c => SolutionFollower.SamePath(c.Root, r11) ? 0 : ServerPicker.PathPolicyFloor;   // C11's server predates #266
                return x;
            };
            pi = floorIn(older11); pi.PinnedVersion = "Clarion 11.0.13244";
            pk = ServerPicker.Pick(pi);
            Check(pk.DowngradeRefused && pk.Server.ExePath == boot.ExePath && pk.Warning == null,
                "a json pin can NOT start a server below the security floor (allowOlderServer default true)", pk.Note);
            Check(Has(pk.Note, "security floor") && Has(pk.Note, "path-policy=" + ServerPicker.PathPolicyFloor),
                "...and the refusal names the floor", pk.Note);

            var newerButOld = CandV(r11, "11.0.0.13244", "6.0.0.1500");   // a NEWER version string, yet no path policy
            pi = floorIn(newerButOld); pi.PinnedVersion = "Clarion 11.0.13244";
            Check(ServerPicker.Pick(pi).DowngradeRefused, "the floor is the CAPABILITY, not the version string (a newer version below the floor is refused)");

            pi = floorIn(newerButOld); pi.ProjectMajors.Add(new KeyValuePair<string, int>("A", 11));
            Check(ServerPicker.Pick(pi).DowngradeRefused, "an inferred choice below the floor is refused");

            pi = guardIn(CandV(r11, "11.0.0.13244", null)); pi.PinnedVersion = "Clarion 11.0.13244";
            pk = ServerPicker.Pick(pi);
            Check(pk.DowngradeRefused && Has(pk.Note, "version unreadable"), "a json pin of a server whose version cannot be read is refused (= below the floor)", pk.Note);

            pi = floorIn(older11); pi.CommandLineVersion = "Clarion 11.0.13244";
            pk = ServerPicker.Pick(pi);
            Check(!pk.DowngradeRefused && pk.Server.Root == r11 && Has(pk.Warning, "security floor") && Has(pk.Warning, "--clarion-version"),
                "only --clarion-version can start a server below the floor, with the WARNING", pk.Warning);

            Check(ServerPicker.ParsePathPolicy(ServerPicker.CapabilitiesText()) == ServerPicker.PathPolicyCapability
                  && ServerPicker.PathPolicyCapability >= ServerPicker.PathPolicyFloor,
                "this build's --capabilities parses back and meets its own floor");
            Check(ServerPicker.ParsePathPolicy("pick-server=2\r\npath-policy=3\r\n") == 3 && ServerPicker.ParsePathPolicy("usage: ...") == 0
                  && ServerPicker.ParsePathPolicy("path-policy=x") == 0, "path-policy parsing: found, absent, malformed");
            // b8f9431c: the IDE proxy is announced, and adding its line leaves the path-policy floor reading unchanged.
            Check(ServerPicker.ParseIdeProxy(ServerPicker.CapabilitiesText()) == ServerPicker.IdeProxyCapability
                  && ServerPicker.IdeProxyCapability >= 1
                  && ServerPicker.ParseIdeProxy("path-policy=2\npick-server=2\n") == 0 && ServerPicker.ParseIdeProxy("ide-proxy=x") == 0,
                "--capabilities announces ide-proxy=1; an older server's output reads as none");
            Check(ServerPicker.ProbePathPolicy(Cand(@"C:\Fake\Nowhere", null)) == 0, "a server that cannot be run reports no capability");
            string notPe = Path.Combine(root, "notpe", ServerPicker.RelativeExe);
            Directory.CreateDirectory(Path.GetDirectoryName(notPe));
            File.WriteAllText(notPe, "placeholder text, not a program - long enough to pass any size check, but no MZ header");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int notPeCap = ServerPicker.ProbePathPolicy(new ServerCandidate { Root = Path.Combine(root, "notpe"), ExePath = notPe });
            sw.Stop();
            Check(notPeCap == 0 && sw.ElapsedMilliseconds < 2000,
                "a non-PE \"server\" is never started (starting one hung CreateProcess from a hidden session)", sw.ElapsedMilliseconds + " ms");

            // The running server's own warning (initialize instructions): needs a real versioned exe - the built server.
            string builtServer = args.Length > 0 ? Path.Combine(args[0], @"mcp-server\bin\Debug\clarion-mcp-server.exe") : null;
            string selfExe = System.Reflection.Assembly.GetExecutingAssembly().Location;   // csc-built: no file version
            if (builtServer != null && File.Exists(builtServer))
            {
                string w = ServerPicker.RunningDowngradeWarning("pick", selfExe, builtServer, null);
                Check(Has(w, "WARNING") && Has(w, "OLDER") && Has(w, builtServer), "running older than the bootstrap gives the warning", w);
                Check(ServerPicker.RunningDowngradeWarning("pick", builtServer, selfExe, null) == null, "running NEWER than the bootstrap gives none");
                Check(ServerPicker.RunningDowngradeWarning("pick", builtServer, builtServer, null) == null, "running AS the bootstrap gives none");
                string i = ServerPicker.DowngradeInstructions(w);
                Check(Has(i, "tell the developer") && Has(i, w), "the initialize instructions carry it and ask the model to tell the developer", i);
                Check(ServerPicker.DowngradeInstructions(null) == null, "no warning, no instructions");
            }
            else
                // Not a pass: ConfigDir.StandaloneE2ETest.ps1 (case 9) builds the server and checks the same warning
                // end to end, through initialize. Here it needs a build to have happened first.
                Console.WriteLine("  skip running-server warning checks: no built clarion-mcp-server.exe at " + (builtServer ?? "(no project dir)")
                    + " - covered by ConfigDir.StandaloneE2ETest.ps1 case 9");

            // ---------------------------------------------------------------- launch note
            Console.WriteLine("serverChosenBy");
            string own = Path.Combine(r11, ServerPicker.RelativeExe);
            var pick11 = new ServerPick { Server = Cand(r11, "11.0.0.13244"), Note = "server under " + r11 + " [chosen by: x]" };
            Func<List<ServerCandidate>> none = () => new List<ServerCandidate>();
            Check(ServerPicker.DescribeLaunch(null, own, () => pick11, none, null) == null, "not started by the launcher: no serverChosenBy");
            string d1 = ServerPicker.DescribeLaunch("pick", own, () => pick11, none, null);
            Check(Has(d1, "clarion-tools launcher") && Has(d1, "chosen by: x") && !Has(d1, "restart"), "a pick that matches this server is reported as is", d1);
            string d2 = ServerPicker.DescribeLaunch("pick", Path.Combine(r12, ServerPicker.RelativeExe), () => pick11, none, null);
            Check(Has(d2, "restart") && Has(d2, r11), "a pick that differs from this server says so", d2);
            Func<ServerPick> mustNotPick = () => { throw new InvalidOperationException("must not pick"); };
            string d3 = ServerPicker.DescribeLaunch("override", own, mustNotPick, none, null);
            Check(Has(d3, "CLARION_MCP_SERVER") && Has(d3, "no pick") && !Has(d3, "WARNING"), "the CLARION_MCP_SERVER override is reported without picking", d3);
            string d3w = ServerPicker.DescribeLaunch("override", own, mustNotPick, () => new List<ServerCandidate> { boot }, null);
            Check(Has(d3w, "WARNING") && Has(d3w, "6.0.0.1400") && Has(d3w, boot.ExePath),
                "a CLARION_MCP_SERVER pin OLDER than an installed server warns, naming the newer one", d3w);
            string d4 = ServerPicker.DescribeLaunch("bootstrap", own, () => pick11, none, null);
            Check(Has(d4, "WITHOUT a pick"), "a launch the bootstrap could not pick is said", d4);
            string bootExe = Path.Combine(r12, ServerPicker.RelativeExe);
            string d5 = ServerPicker.DescribeLaunch("fallback", bootExe, () => pick11, none, own);
            Check(Has(d5, "FALLBACK") && Has(d5, "failed to start") && Has(d5, own) && !Has(d5, "restart"),
                "a fallback after the pick failed to start names the failed server", d5);
        }
        finally
        {
            try { IdeSolutionRecord.RootOverride = null; } catch { }
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        if (_failures == 0) { Console.WriteLine("PASS - " + _assertions + " assertions"); return 0; }
        Console.WriteLine("FAIL - " + _failures + " of " + _assertions + " assertions");
        return 1;
    }
}
