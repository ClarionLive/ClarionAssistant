using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using ClarionAssistant.Services;

// Consistency harness for Terminal\models.json, the model registry behind the Plan/Model pickers in
// Settings, and for the retired-model remap that reads it (SettingsService.ResolveRetiredModel).
// Ticket 0f928849: a Team-plan user couldn't pick Opus 5.5 because the registry had gone six months
// stale, and Copilot/Codex had retired most of the ids it offered.
//
// Compiles the REAL Services\SettingsService.cs and reads the REAL shipped models.json.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it - it has its own Main().
//
// Argument 1 (required, passed by Run-Tests.ps1): the ClarionAssistant project dir.
//
// PROVEN ABLE TO FAIL, each mutation applied alone to a copy:
//   Copilot claude-opus-4.7 -> nonexistent model + CodexDefaultModelId back to gpt-5.5  -> 4 red
//   Copilot gemini-2.5-pro -> "" (Default, not offered on Free)                          -> 1 red
//   IsUsableRegistry skipping its schema checks                                           -> 4 red
//   GetInstalledClaudeVersion calling FindClaudePath() (PATH search allowed)              -> 1 red
// (Truncated / trailing-comma registries stay rejected under the schema mutant: the parser
// throws first. That is correct, not a gap.)
static class ModelRegistryTest
{
    static int pass = 0, fail = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    static Dictionary<string, object> D(object o) { return o as Dictionary<string, object>; }

    static int Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("usage: ModelRegistry.Test <ClarionAssistant project dir>"); return 2; }
        string projectDir = args[0];
        string registryPath = Path.Combine(projectDir, "Terminal", "models.json");
        string json = File.ReadAllText(registryPath);

        var root = D(new JavaScriptSerializer().DeserializeObject(json));
        Ok("models.json parses", root != null);
        if (root == null) return 1;
        var backends = D(root["backends"]);
        Ok("has the three backends", backends != null && backends.ContainsKey("Claude")
            && backends.ContainsKey("Copilot") && backends.ContainsKey("Codex"));

        var semver = new Regex(@"^\d+\.\d+\.\d+$");
        foreach (var kv in backends)
        {
            string b = kv.Key;
            var info = D(kv.Value);
            var planIds = new HashSet<string>();
            foreach (var p in (object[])info["plans"]) planIds.Add((string)D(p)["id"]);
            Ok(b + ": defaultPlan is a declared plan", planIds.Contains((string)info["defaultPlan"]));

            var modelIds = new HashSet<string>();
            var plansWithAModel = new HashSet<string>();
            foreach (var mo in (object[])info["models"])
            {
                var m = D(mo);
                string id = (string)m["id"];
                Ok(b + ": model id '" + id + "' is unique", modelIds.Add(id));
                foreach (var po in (object[])m["plans"])
                {
                    Ok(b + ": '" + id + "' names declared plan '" + po + "'", planIds.Contains((string)po));
                    plansWithAModel.Add((string)po);
                }
                if (m.ContainsKey("minCliVersion"))
                    Ok(b + ": '" + id + "' minCliVersion is x.y.z", semver.IsMatch((string)m["minCliVersion"]),
                        (string)m["minCliVersion"]);
            }
            foreach (var p in planIds)
                Ok(b + ": plan '" + p + "' offers at least one model", plansWithAModel.Contains(p));

            var retired = info.ContainsKey("retired") ? D(info["retired"]) : new Dictionary<string, object>();
            foreach (var r in retired)
            {
                string to = (string)r.Value;
                Ok(b + ": retired '" + r.Key + "' is not still offered", !modelIds.Contains(r.Key));
                Ok(b + ": retired '" + r.Key + "' -> '" + to + "' is an offered model", modelIds.Contains(to));
                string once = SettingsService.ResolveRetiredModel(json, b, r.Key);
                Ok(b + ": resolve('" + r.Key + "') = '" + to + "'", once == to, once);
                Ok(b + ": resolve is idempotent for '" + r.Key + "'",
                    SettingsService.ResolveRetiredModel(json, b, once) == once);
            }
        }

        // The cases the ticket was about, spelled out so a registry edit that drops them goes red.
        Ok("Claude offers Opus 5.5 on Team", OffersOnPlan(backends, "Claude", "opus", "Team"));
        Ok("Copilot claude-opus-4.7 -> claude-opus-5.5",
            SettingsService.ResolveRetiredModel(json, "Copilot", "claude-opus-4.7") == "claude-opus-5.5");
        Ok("Codex gpt-5.5 -> gpt-6.1-sol",
            SettingsService.ResolveRetiredModel(json, "Codex", "gpt-5.5") == "gpt-6.1-sol");
        Ok("Claude opus[1m] -> opus",
            SettingsService.ResolveRetiredModel(json, "Claude", "opus[1m]") == "opus");
        Ok("surrounding whitespace still matches",
            SettingsService.ResolveRetiredModel(json, "Codex", "  gpt-5.5 ") == "gpt-6.1-sol");

        // Pass-through cases: a live id, a custom id, an empty value, a retired id under the wrong
        // backend, and an unreadable registry all come back untouched.
        Ok("live id unchanged", SettingsService.ResolveRetiredModel(json, "Claude", "opus") == "opus");
        Ok("custom id unchanged",
            SettingsService.ResolveRetiredModel(json, "Claude", "claude-opus-5-5") == "claude-opus-5-5");
        Ok("empty unchanged", SettingsService.ResolveRetiredModel(json, "Copilot", "") == "");
        Ok("other backend's retired id unchanged",
            SettingsService.ResolveRetiredModel(json, "Claude", "gpt-5.5") == "gpt-5.5");
        Ok("bad registry -> unchanged",
            SettingsService.ResolveRetiredModel("{not json", "Codex", "gpt-5.5") == "gpt-5.5");

        // A Free Copilot user's retired model must land on a model Free actually offers.
        Ok("Copilot gemini-2.5-pro -> a model offered on Free",
            OffersOnPlan(backends, "Copilot", SettingsService.ResolveRetiredModel(json, "Copilot", "gemini-2.5-pro"), "Free"));

        // A hand-broken deployed models.json must be rejected so the loader falls back to the
        // embedded copy (the Settings page splices the text into its JSON message as-is).
        Ok("shipped registry is usable", SettingsService.IsUsableRegistry(json));
        Ok("truncated registry rejected", !SettingsService.IsUsableRegistry(json.Substring(0, json.Length / 2)));
        Ok("trailing-comma registry rejected", !SettingsService.IsUsableRegistry(json.TrimEnd().TrimEnd('}') + ",}"));
        Ok("empty registry rejected", !SettingsService.IsUsableRegistry(""));
        Ok("no-backends registry rejected", !SettingsService.IsUsableRegistry("{\"version\":\"1\"}"));
        Ok("empty-backends registry rejected", !SettingsService.IsUsableRegistry("{\"backends\":{}}"));
        Ok("backend without models rejected",
            !SettingsService.IsUsableRegistry("{\"backends\":{\"Claude\":{\"plans\":[]}}}"));

        // Opening Settings runs `claude --version`; that probe must never take a binary from a
        // PATH / current-directory search (Codex security gate, run 1).
        string cpm = File.ReadAllText(Path.Combine(projectDir, "Services", "ClaudeProcessManager.cs"));
        var probe = Regex.Match(cpm, @"public static string GetInstalledClaudeVersion\(.*?\n        }\r?\n", RegexOptions.Singleline);
        Ok("GetInstalledClaudeVersion found", probe.Success);
        Ok("version probe resolves with allowPathSearch: false",
            probe.Success && probe.Value.Contains("FindClaudePath(allowPathSearch: false)")
            && !Regex.IsMatch(probe.Value, @"FindClaudePath\(\s*\)"));

        // The Codex launcher's hard-coded default must be the registry's first (Default) Codex entry,
        // and never an id the registry has retired.
        string acc = File.ReadAllText(Path.Combine(projectDir, "AssistantChatControl.cs"));
        var cm = Regex.Match(acc, "CodexDefaultModelId\\s*=\\s*\"([^\"]+)\"");
        Ok("CodexDefaultModelId found", cm.Success);
        if (cm.Success)
        {
            string codexDefault = cm.Groups[1].Value;
            var firstCodex = D(((object[])D(backends["Codex"])["models"])[0]);
            Ok("CodexDefaultModelId '" + codexDefault + "' is the first Codex model",
                codexDefault == (string)firstCodex["id"], (string)firstCodex["id"]);
            Ok("CodexDefaultModelId is not retired",
                SettingsService.ResolveRetiredModel(json, "Codex", codexDefault) == codexDefault);
        }

        Console.WriteLine();
        Console.WriteLine(pass + " passed, " + fail + " failed");
        return fail == 0 ? 0 : 1;
    }

    static bool OffersOnPlan(Dictionary<string, object> backends, string backend, string id, string plan)
    {
        foreach (var mo in (object[])D(backends[backend])["models"])
        {
            var m = D(mo);
            if ((string)m["id"] != id) continue;
            foreach (var p in (object[])m["plans"]) if ((string)p == plan) return true;
        }
        return false;
    }
}
