using System;
using System.Collections.Generic;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Merges the user's mcp-extra.json into the addin's generated server list.
    ///
    /// EXTRACTED SO IT CAN BE TESTED. It lived inside McpServer.GenerateMcpConfig, which needs a
    /// WinForms Control and an HttpListener and so cannot be exercised outside the IDE. The rule
    /// it enforces is worth testing, because getting it wrong fails SILENTLY and in the most
    /// misleading way available: the pane would keep working, while quietly talking to whatever
    /// executable and whatever solution the sidecar happened to name.
    ///
    /// THE RULE IS POSITIONAL, WHICH IS THE RISK. A key is reserved simply by already being in the
    /// dictionary — there is no list of reserved names, and deliberately so, because a list would
    /// be a second place to update every time the addin gains a server, and the day someone
    /// forgot, the sidecar could override a real one. The cost of that choice is an ORDERING
    /// INVARIANT: every addin-supplied server must be added BEFORE this runs. Move this call
    /// earlier, or add a new addin server after it, and the sidecar wins.
    ///
    /// That invariant was found by CC testing the live IDE, not by the build: clarion-tools was
    /// protected only because the addin's injection happened to run first. It still is — but now
    /// there is a test that fails if the order changes.
    /// </summary>
    public static class McpSidecarMerge
    {
        /// <summary>
        /// Merge <paramref name="sidecarJson"/> into <paramref name="servers"/>, skipping any key
        /// the addin already supplied. Returns the names actually merged, in encounter order —
        /// the caller grants those <c>mcp__&lt;name&gt;__*</c> so a user's own servers auto-approve
        /// like the built-ins. A rejected key MUST NOT appear in that list, or a reserved name
        /// would be granted on the strength of an entry that was discarded.
        /// </summary>
        /// <param name="log">Optional diagnostic sink; receives one line per key, kept or skipped.</param>
        public static List<string> Merge(
            Dictionary<string, object> servers,
            string sidecarJson,
            Action<string> log = null)
        {
            var merged = new List<string>();
            if (servers == null || string.IsNullOrEmpty(sidecarJson)) return merged;

            Dictionary<string, object> parsed;
            try
            {
                parsed = McpJsonRpc.Deserialize(sidecarJson);
            }
            catch (Exception ex)
            {
                // A malformed sidecar must not take the pane down with it: the user's own file is
                // the least trustworthy input here, and losing every MCP server because of a
                // stray comma would be wildly out of proportion.
                if (log != null) log("mcp-extra.json is not valid JSON (ignored): " + ex.Message);
                return merged;
            }

            object entriesObj;
            if (parsed == null || !parsed.TryGetValue("mcpServers", out entriesObj)) return merged;
            var entries = entriesObj as Dictionary<string, object>;
            if (entries == null) return merged;

            foreach (var kv in entries)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (servers.ContainsKey(kv.Key))
                {
                    if (log != null) log("skipping reserved key '" + kv.Key + "'");
                    continue;
                }
                servers[kv.Key] = kv.Value;
                merged.Add(kv.Key);
                if (log != null) log("merged server '" + kv.Key + "'");
            }
            return merged;
        }
    }
}
