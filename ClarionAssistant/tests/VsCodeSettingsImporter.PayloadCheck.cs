using System;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using ClarionAssistant.Services;

// Verifies ToBridgePayload serializes to exactly the JSON shape the page's response handler will read.
// The bridge glue (VsCodeImportBridge) is IDE-coupled and can't be compiled standalone, but the payload
// it sends comes from here — so this is the part of the bridge that IS testable outside Clarion.
//
// Run:  tests\Run-Tests.ps1
//
// This is the C# half of a contract that has a JS half: Terminal/test/vscode-import-ui.test.js asserts
// how the PAGE reads {found, path, source, error, values, skipped, cancelled}. Change the shape on one
// side and the other side's test is what tells you. In particular this pins two distinctions that are
// easy to collapse and expensive to get wrong: error must be "" rather than null on success, and
// cancelled must stay separable from not-found (an abandoned Browse dialog is not a missing file).
//
// Not in ClarionAssistant.csproj — it has its own Main(). See the note in the SmokeTest header.
static class PayloadCheck
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    static int Main()
    {
        var ser = new JavaScriptSerializer();

        // --- populated result ---
        string tmp = Path.Combine(Path.GetTempPath(), "vscode-payload-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(tmp, "{ \"editor.tabSize\": 4, \"editor.fontFamily\": \"'Fira Code', monospace\", \"editor.wordWrap\": \"nonsense\" }");
        var r = VsCodeSettingsImporter.Read(tmp);
        var payload = VsCodeSettingsImporter.ToBridgePayload(r);
        payload["cancelled"] = false;                       // the bridge stamps this
        string json = ser.Serialize(payload);
        Console.WriteLine("populated payload:");
        Console.WriteLine("  " + json);
        Console.WriteLine();

        var back = ser.DeserializeObject(json) as Dictionary<string, object>;
        Ok("round-trips as an object", back != null, null);
        foreach (var key in new[] { "found", "path", "source", "error", "values", "skipped", "cancelled" })
            Ok("has key '" + key + "'", back.ContainsKey(key), null);

        Ok("found is a bool", back["found"] is bool, back["found"] == null ? "null" : back["found"].GetType().Name);
        Ok("cancelled is a bool", back["cancelled"] is bool, null);
        Ok("values is an object", back["values"] is Dictionary<string, object>, null);
        Ok("skipped is an array", back["skipped"] is object[], null);
        Ok("error is '' not null when successful", (back["error"] as string) == "", "got " + back["error"]);

        var values = (Dictionary<string, object>)back["values"];
        Ok("values.tabSize survives serialization as 4", Equals(values["tabSize"], 4), "got " + values["tabSize"]);
        Ok("values.fontFamily unquoted to 'Fira Code'", Equals(values["fontFamily"], "Fira Code"), "got " + values["fontFamily"]);
        Ok("bad wordWrap did NOT reach values", !values.ContainsKey("wordWrap"), null);

        var skipped = (object[])back["skipped"];
        Ok("bad wordWrap IS reported in skipped", skipped.Length == 1, "len=" + skipped.Length);
        if (skipped.Length == 1)
        {
            var s0 = skipped[0] as Dictionary<string, object>;
            Ok("skipped entry has key+reason", s0 != null && s0.ContainsKey("key") && s0.ContainsKey("reason"), null);
            Ok("skipped names editor.wordWrap", s0 != null && Equals(s0["key"], "editor.wordWrap"), s0 == null ? null : "" + s0["key"]);
        }

        // --- the two empty shapes the bridge also sends ---
        Console.WriteLine();
        var notFound = VsCodeSettingsImporter.ToBridgePayload(new VsCodeSettingsImporter.Result());
        notFound["cancelled"] = false;
        string nfJson = ser.Serialize(notFound);
        Console.WriteLine("not-found payload : " + nfJson);
        var nf = ser.DeserializeObject(nfJson) as Dictionary<string, object>;
        Ok("not-found: found=false", Equals(nf["found"], false), null);
        Ok("not-found: empty values object (not null)", nf["values"] is Dictionary<string, object>, null);
        Ok("not-found: path is '' not null", (nf["path"] as string) == "", null);

        var cancelledPayload = VsCodeSettingsImporter.ToBridgePayload(new VsCodeSettingsImporter.Result());
        cancelledPayload["cancelled"] = true;
        var cn = ser.DeserializeObject(ser.Serialize(cancelledPayload)) as Dictionary<string, object>;
        Console.WriteLine("cancelled payload : " + ser.Serialize(cancelledPayload));
        Ok("cancelled is distinguishable from not-found", Equals(cn["cancelled"], true) && Equals(cn["found"], false), null);

        // Null-safety: the bridge calls ToBridgePayload(null) on no path at all.
        Ok("ToBridgePayload(null) does not throw", VsCodeSettingsImporter.ToBridgePayload(null) != null, null);

        try { File.Delete(tmp); } catch { }

        Console.WriteLine();
        Console.WriteLine(fail == 0 ? ("ALL PASS (" + pass + ")") : (fail + " FAILED, " + pass + " passed"));
        return fail == 0 ? 0 : 1;
    }
}
