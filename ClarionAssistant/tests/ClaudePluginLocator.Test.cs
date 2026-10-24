using System;
using System.IO;
using ClarionAssistant.Services;

// Harness for GH #214: ONE resolver for the --plugin-dir folders (Clarion Assistant, MultiTerminal),
// honouring a shared marketplace folder and the active Claude profile, and picking the newest cached
// version instead of the hard-coded "1.0.0" (ticket 7c36dd75).
//
// Compiles the REAL Services\ClaudePluginLocator.cs and drives it with explicit Inputs against fake
// profiles under %TEMP%. Never reads the real profile, never reads or writes the registry.
//
// Argument 1 (passed by Run-Tests.ps1): the ClarionAssistant project dir, for the source checks that
// the three former copies of the lookup now all go through the resolver.
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it - it has its own Main().
static class ClaudePluginLocatorTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    const string Mkt = ClaudePluginLocator.ClarionAssistantMarketplace;
    const string Plg = ClaudePluginLocator.ClarionAssistantPlugin;

    static string MakePlugin(string dir, string version)
    {
        Directory.CreateDirectory(Path.Combine(dir, ".claude-plugin"));
        string json = version == null
            ? "{ \"name\": \"x\" }"
            : "{\r\n  \"name\": \"x\",\r\n  \"version\": \"" + version + "\"\r\n}";
        File.WriteAllText(Path.Combine(dir, ".claude-plugin", "plugin.json"), json);
        return dir;
    }

    static string MarketplacePlugin(string profile, string mkt, string plugin)
    {
        return Path.Combine(profile, "plugins", "marketplaces", mkt, "plugins", plugin);
    }

    static string CacheDir(string profile, string mkt, string plugin)
    {
        return Path.Combine(profile, "plugins", "cache", mkt, plugin);
    }

    static string Got(ClaudePluginLocator.Inputs i) { return ClaudePluginLocator.Find(Mkt, Plg, i); }

    static bool Same(string a, string b)
    {
        return a != null && b != null &&
               string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-pluginloc-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            string user = Path.Combine(root, "Users", "Someone");
            string defProfile = Path.Combine(user, ".claude");
            string altProfile = Path.Combine(user, ".claude-work");
            string sharedRoot = Path.Combine(root, "Local", "ClarionLive", "claude-plugins", "marketplaces");
            string shared = Path.Combine(sharedRoot, Mkt);

            // --- 1. Shared path set AND present: it wins over a default-profile copy.
            string defMkt = MakePlugin(MarketplacePlugin(defProfile, Mkt, Plg), "2.1.0");
            string sharedPlugin = MakePlugin(Path.Combine(shared, "plugins", Plg), "2.1.0");
            var i1 = new ClaudePluginLocator.Inputs { SharedMarketplaceDir = shared, UserProfile = user };
            Ok("shared set and present -> shared plugin folder", Same(Got(i1), sharedPlugin), Got(i1));
            var r1 = ClaudePluginLocator.Resolve(Mkt, Plg, i1);
            Ok("shared set and present -> tier 'shared'", r1 != null && r1.Tier == "shared", r1 == null ? "null" : r1.Tier);

            // The registry may hold %VARS% and quotes; both are normalised.
            Environment.SetEnvironmentVariable("CA_PLUGINLOC_TEST_ROOT", root);
            var i1b = new ClaudePluginLocator.Inputs
            {
                SharedMarketplaceDir = "\"%CA_PLUGINLOC_TEST_ROOT%\\Local\\ClarionLive\\claude-plugins\\marketplaces\\" + Mkt + "\\\"",
                UserProfile = user
            };
            Ok("shared path with %VAR%, quotes and trailing slash -> shared plugin", Same(Got(i1b), sharedPlugin), Got(i1b));

            // --- 2. Shared path set but MISSING: falls back to the profile, never null.
            var i2 = new ClaudePluginLocator.Inputs { SharedMarketplaceDir = Path.Combine(root, "nowhere", Mkt), UserProfile = user };
            Ok("shared set but missing -> falls back to default profile marketplace", Same(Got(i2), defMkt), Got(i2));
            // Present folder that is NOT a plugin (no plugin.json) is not a hit either.
            string hollow = Path.Combine(root, "hollow", Mkt);
            Directory.CreateDirectory(Path.Combine(hollow, "plugins", Plg));
            var i2b = new ClaudePluginLocator.Inputs { SharedMarketplaceDir = hollow, UserProfile = user };
            Ok("shared folder without plugin.json -> falls back", Same(Got(i2b), defMkt), Got(i2b));
            // Relative path in the registry is ignored, not resolved against the IDE's cwd.
            var i2c = new ClaudePluginLocator.Inputs { SharedMarketplaceDir = "relative\\" + Mkt, UserProfile = user };
            Ok("relative shared path ignored -> default profile", Same(Got(i2c), defMkt), Got(i2c));

            // --- 3. CLAUDE_CONFIG_DIR set: the ACTIVE profile beats the default one.
            string altMkt = MakePlugin(MarketplacePlugin(altProfile, Mkt, Plg), "2.1.0");
            var i3 = new ClaudePluginLocator.Inputs { ClaudeConfigDir = altProfile, UserProfile = user };
            Ok("CLAUDE_CONFIG_DIR set -> that profile's marketplace", Same(Got(i3), altMkt), Got(i3));
            // ...and shared still beats it.
            var i3b = new ClaudePluginLocator.Inputs { SharedMarketplaceDir = shared, ClaudeConfigDir = altProfile, UserProfile = user };
            Ok("shared + CLAUDE_CONFIG_DIR -> shared first", Same(Got(i3b), sharedPlugin), Got(i3b));
            // CLAUDE_CONFIG_DIR naming an empty profile falls back to %USERPROFILE%\.claude (today's behaviour).
            string emptyProfile = Path.Combine(user, ".claude-empty");
            Directory.CreateDirectory(emptyProfile);
            var i3c = new ClaudePluginLocator.Inputs { ClaudeConfigDir = emptyProfile, UserProfile = user };
            Ok("CLAUDE_CONFIG_DIR without the plugin -> default profile", Same(Got(i3c), defMkt), Got(i3c));
            var roots = ClaudePluginLocator.ProfileRoots(new ClaudePluginLocator.Inputs { ClaudeConfigDir = defProfile + "\\", UserProfile = user });
            Ok("CLAUDE_CONFIG_DIR = default profile -> probed once", roots.Count == 1, string.Join(" | ", roots));

            // --- 4. Cache: version read from plugin.json (folder name says nothing useful).
            string p4 = Path.Combine(user, ".claude-cache1");
            string c4 = CacheDir(p4, Mkt, Plg);
            MakePlugin(Path.Combine(c4, "1.0.0"), "1.0.0");
            string shaFolder = MakePlugin(Path.Combine(c4, "a1b2c3d4e5f6"), "2.1.0");
            Directory.SetLastWriteTimeUtc(Path.Combine(c4, "1.0.0"), DateTime.UtcNow.AddHours(1));   // newer on disk, older version
            var i4 = new ClaudePluginLocator.Inputs { ClaudeConfigDir = p4, UserProfile = Path.Combine(root, "nouser") };
            Ok("cache: version from plugin.json (2.1.0 in a sha folder beats 1.0.0)", Same(Got(i4), shaFolder), Got(i4));
            var r4 = ClaudePluginLocator.Resolve(Mkt, Plg, i4);
            Ok("cache: tier 'profile cache'", r4 != null && r4.Tier == "profile cache", r4 == null ? "null" : r4.Tier);
            Ok("ReadPluginVersion reads plugin.json", ClaudePluginLocator.ReadPluginVersion(shaFolder) == "2.1.0",
               ClaudePluginLocator.ReadPluginVersion(shaFolder));

            // --- 5. Cache: newest version FOLDER when plugin.json has no version; numeric, not lexical.
            string p5 = Path.Combine(user, ".claude-cache2");
            string c5 = CacheDir(p5, Mkt, Plg);
            MakePlugin(Path.Combine(c5, "1.9.0"), null);
            string v210 = MakePlugin(Path.Combine(c5, "2.10.0"), null);
            MakePlugin(Path.Combine(c5, "2.9.1"), null);
            Directory.CreateDirectory(Path.Combine(c5, "9.9.9"));   // newest name, but not a plugin
            var i5 = new ClaudePluginLocator.Inputs { ClaudeConfigDir = p5, UserProfile = Path.Combine(root, "nouser") };
            Ok("cache: newest version folder (2.10.0 > 2.9.1 > 1.9.0; non-plugin 9.9.9 skipped)", Same(Got(i5), v210), Got(i5));

            // The old lookup only ever matched a folder named exactly 1.0.0. A cache with just the
            // CURRENT version must be found.
            string p5b = Path.Combine(user, ".claude-cache3");
            string only = MakePlugin(Path.Combine(CacheDir(p5b, Mkt, Plg), "2.1.0"), "2.1.0");
            var i5b = new ClaudePluginLocator.Inputs { ClaudeConfigDir = p5b, UserProfile = Path.Combine(root, "nouser") };
            Ok("cache: a lone 2.1.0 folder is found (no 1.0.0 needed)", Same(Got(i5b), only), Got(i5b));

            // Marketplace beats cache inside one profile.
            MakePlugin(Path.Combine(CacheDir(altProfile, Mkt, Plg), "9.0.0"), "9.0.0");
            Ok("marketplace beats cache in the same profile", Same(Got(i3), altMkt), Got(i3));

            // Nothing anywhere -> null, not an exception.
            var i6 = new ClaudePluginLocator.Inputs { SharedMarketplaceDir = Path.Combine(root, "x"), ClaudeConfigDir = Path.Combine(root, "y"), UserProfile = Path.Combine(root, "z") };
            Ok("nothing installed -> null", Got(i6) == null, Got(i6));

            // --- 7. MultiTerminal: looked for BESIDE the shared Clarion Assistant marketplace.
            string mtShared = MakePlugin(Path.Combine(sharedRoot, ClaudePluginLocator.MultiTerminalMarketplace, "plugins",
                ClaudePluginLocator.MultiTerminalPlugin), "1.0.0");
            string mtDef = MakePlugin(MarketplacePlugin(defProfile, ClaudePluginLocator.MultiTerminalMarketplace,
                ClaudePluginLocator.MultiTerminalPlugin), "1.0.0");
            string mtGot = ClaudePluginLocator.Find(ClaudePluginLocator.MultiTerminalMarketplace, ClaudePluginLocator.MultiTerminalPlugin, i1);
            Ok("MultiTerminal: sibling of the shared CA marketplace", Same(mtGot, mtShared), mtGot);
            string custom = Path.Combine(root, "MyPlugins");
            var i7 = new ClaudePluginLocator.Inputs { SharedMarketplaceDir = custom, UserProfile = user };
            string mtGot2 = ClaudePluginLocator.Find(ClaudePluginLocator.MultiTerminalMarketplace, ClaudePluginLocator.MultiTerminalPlugin, i7);
            Ok("MultiTerminal: custom shared leaf -> no guessed sibling, default profile", Same(mtGot2, mtDef), mtGot2);
            Ok("MultiTerminal: returns the PLUGIN folder, not its parent",
               mtGot != null && Path.GetFileName(mtGot) == ClaudePluginLocator.MultiTerminalPlugin, mtGot);

            // --- 8. Source: the three former copies now all go through the resolver.
            if (args.Length > 0 && Directory.Exists(args[0]))
            {
                string proj = args[0];
                string chat = File.ReadAllText(Path.Combine(proj, "AssistantChatControl.cs"));
                string cpm = File.ReadAllText(Path.Combine(proj, "Services", "ClaudeProcessManager.cs"));
                string mcp = File.ReadAllText(Path.Combine(proj, "Services", "McpServer.cs"));
                Ok("AssistantChatControl uses ClaudePluginLocator", chat.Contains("ClaudePluginLocator.FindClarionAssistantPlugin()"));
                Ok("ClaudeProcessManager uses ClaudePluginLocator", cpm.Contains("ClaudePluginLocator.FindClarionAssistantPlugin()"));
                Ok("McpServer uses ClaudePluginLocator", mcp.Contains("ClaudePluginLocator.FindMultiTerminalPlugin()"));
                foreach (var f in new[] { new { N = "AssistantChatControl.cs", T = chat }, new { N = "ClaudeProcessManager.cs", T = cpm }, new { N = "McpServer.cs", T = mcp } })
                {
                    Ok(f.N + ": no hard-coded plugin cache version",
                       !System.Text.RegularExpressions.Regex.IsMatch(f.T, "\"cache\",\\s*\"[^\"]+\",\\s*\"[^\"]+\",\\s*\"1\\.0\\.0\""));
                    Ok(f.N + ": no hard-coded \"plugins\", \"marketplaces\" profile probe",
                       !System.Text.RegularExpressions.Regex.IsMatch(f.T, "\"\\.claude\",\\s*\"plugins\",\\s*\"(marketplaces|cache)\""));
                }
                string csproj = File.ReadAllText(Path.Combine(proj, "ClarionAssistant.csproj"));
                Ok("ClarionAssistant.csproj compiles ClaudePluginLocator.cs", csproj.Contains("Services\\ClaudePluginLocator.cs"));
            }
            else
            {
                Console.WriteLine("  (project dir not passed - source checks skipped)");
                fail++;
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
        Console.WriteLine("ClaudePluginLocator.Test: " + pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }
}
