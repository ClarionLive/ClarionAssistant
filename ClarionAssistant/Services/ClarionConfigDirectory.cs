using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Which settings folder is THIS Clarion actually using? (GitHub #197)
    ///
    /// Clarion.exe accepts <c>/ConfigDir=&lt;path&gt;</c> on the command line, which relocates the whole
    /// per-environment settings tree — ClarionProperties.xml included — away from the default
    /// <c>%APPDATA%\SoftVelocity\Clarion\&lt;major&gt;.&lt;minor&gt;</c>. Developers use it to run several
    /// installs side by side without them sharing state (verified in Clarion.exe's string heap, where the
    /// literal "ConfigDir=" sits directly beside "SoftVelocity", "Clarion", "12.0" and "Settings" — the
    /// components of the default path it is choosing between — and the binary also carries
    /// set_ConfigDirectory).
    ///
    /// CA used to ignore the switch entirely and rebuild the default path from scratch, so under
    /// /ConfigDir it read a DIFFERENT environment's ClarionProperties.xml. That is worse than it sounds:
    /// Clarion 11 and 11.1 both stamp FileMajorPart.FileMinorPart as "11.0", so two such environments
    /// resolved to the SAME file, produced the same install root, and therefore the same VersionTag —
    /// which is what merged their histories.
    ///
    /// ORDER OF AUTHORITY, and why:
    ///   1. PropertyService.ConfigDirectory — the IDE's own resolved answer. It is get-only and is fed by
    ///      InitializeService(configDirectory, dataDirectory, propertiesName) at startup, so it already
    ///      accounts for the switch however Clarion chose to parse it. Verified by reflection over
    ///      ICSharpCode.Core.dll rather than assumed.
    ///   2. Our own parse of /ConfigDir= off the command line — ONLY a pre-initialisation fallback. Addin
    ///      code can run before the IDE finishes starting, and a null answer there would otherwise be
    ///      cached for the session.
    ///   3. null — caller keeps its existing default behaviour. Never guess a path here.
    /// </summary>
    internal static class ClarionConfigDirectory
    {
        private static readonly object Gate = new object();
        private static string _cached;
        private static bool _isCached;
        private static bool _logged;

        /// <summary>
        /// The effective config directory, or null when it could not be established (caller should fall
        /// back to its own default). Never throws.
        /// </summary>
        public static string Resolve()
        {
            lock (Gate)
            {
                if (_isCached) return _cached;

                // (2) first, because it is cheap, deterministic for the life of the process, and — unlike
                // PropertyService — available before the IDE has initialised. If the switch is present it
                // IS the answer; there is nothing for PropertyService to disagree with.
                string fromArgs = FromCommandLine();
                if (!string.IsNullOrEmpty(fromArgs))
                    return Cache(fromArgs);

                // (1) No switch on the command line: ask the IDE what it settled on. Only CACHE this once
                // PropertyService reports Initialized — otherwise an early caller would freeze a null
                // (or a half-built value) for the whole session, which is exactly the kind of
                // machine-dependent, intermittent bug that is worse than the one being fixed.
                bool initialized;
                string fromIde = FromPropertyService(out initialized);
                if (!initialized) return Normalize(fromIde);   // answer now, decide again later
                return Cache(fromIde);
            }
        }

        /// <summary>
        /// Has resolution reached a STABLE answer — one that cannot change later in this process?
        ///
        /// True once either the command-line switch has been seen (fixed for the process lifetime) or
        /// PropertyService has reported Initialized. Callers that memoise a value derived from the config
        /// directory must gate their cache on this, or an addin call made during IDE startup pins a
        /// pre-initialisation answer for the whole session.
        /// </summary>
        public static bool IsResolvable()
        {
            Resolve();                       // may promote the answer to cached
            lock (Gate) { return _isCached; }
        }

        /// <summary>
        /// True when this environment runs on a config directory OUTSIDE the default
        /// %APPDATA%\SoftVelocity\Clarion tree — i.e. someone passed /ConfigDir.
        ///
        /// This predicate is what keeps the fix migration-free: on a default install it is false, so
        /// VersionTag() is byte-identical to what it has always produced and nobody's existing history
        /// moves. Only a custom-settings environment gets a new folder.
        /// </summary>
        public static bool IsNonDefault()
        {
            string cfg = Resolve();
            if (string.IsNullOrEmpty(cfg)) return false;

            string defaultRoot = DefaultConfigRoot();
            if (string.IsNullOrEmpty(defaultRoot)) return false;

            // Prefix match with a separator guard so "...\ClarionX" is not read as inside "...\Clarion".
            string a = cfg.TrimEnd('\\', '/');
            string b = defaultRoot.TrimEnd('\\', '/');
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return false;
            return !a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Folder-name suffix that separates this environment from others: "" on a default install,
        /// otherwise "~" plus six hex characters derived from the config directory path.
        ///
        /// A hash rather than the path itself because the path is arbitrary, can be long, and can collide
        /// on its leaf name (two people both using "...\c11.Settings" under different roots). Six hex
        /// characters is 16.7M buckets against the handful of environments one machine ever has — and a
        /// collision degrades to today's shared-folder behaviour, it does not corrupt anything.
        /// </summary>
        public static string Discriminator()
        {
            if (!IsNonDefault()) return "";
            string cfg = Resolve();
            if (string.IsNullOrEmpty(cfg)) return "";
            try
            {
                using (var sha = SHA1.Create())
                {
                    byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(cfg.TrimEnd('\\', '/').ToLowerInvariant()));
                    var sb = new StringBuilder(6);
                    for (int i = 0; i < 3; i++) sb.Append(h[i].ToString("x2"));
                    return "~" + sb;
                }
            }
            catch { return ""; }
        }

        /// <summary>
        /// One line to monaco-spike.log naming what we resolved and whether it counted as non-default.
        /// Written at most once per process. This exists because #197 has no local reproduction — the
        /// only person who can confirm the fix runs a dual-environment setup we do not have, so they need
        /// something concrete to read back rather than an impression that things "look separate now".
        /// </summary>
        public static void LogOnce()
        {
            lock (Gate)
            {
                if (_logged) return;
                _logged = true;
            }
            try
            {
                string cfg = Resolve();
                MonacoSpikeLog.Write(string.Format(
                    "[config-dir] resolved={0} nonDefault={1} discriminator='{2}' default={3}",
                    string.IsNullOrEmpty(cfg) ? "(none — using built-in default)" : cfg,
                    IsNonDefault(),
                    Discriminator(),
                    DefaultConfigRoot() ?? "(unknown)"));
            }
            catch { }
        }

        /// <summary>%APPDATA%\SoftVelocity\Clarion — the root the default config directory lives under.</summary>
        private static string DefaultConfigRoot()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(appData)) return null;
                return Path.Combine(appData, "SoftVelocity", "Clarion");
            }
            catch { return null; }
        }

        /// <summary>
        /// Scan the process command line for the switch. Accepts "/ConfigDir=X" and "-ConfigDir=X"
        /// case-insensitively, quoted or bare. Clarion's own literal is "ConfigDir=" (capital D) but the
        /// switch is documented and typed by humans in lower case, so match without regard to case.
        /// </summary>
        private static string FromCommandLine()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                if (args == null) return null;
                for (int i = 1; i < args.Length; i++)   // skip argv[0], the exe path
                {
                    string a = args[i];
                    if (string.IsNullOrEmpty(a)) continue;
                    if (a[0] != '/' && a[0] != '-') continue;

                    string body = a.Substring(1);
                    const string key = "ConfigDir=";
                    if (!body.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;

                    string val = body.Substring(key.Length).Trim().Trim('"');
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// PropertyService.ConfigDirectory by reflection (the addin must not hard-reference a
        /// version-specific ICSharpCode.Core — see the strong-name version-lock gotcha).
        /// <paramref name="initialized"/> reports PropertyService.Initialized so the caller can decide
        /// whether the answer is stable enough to cache.
        /// </summary>
        private static string FromPropertyService(out bool initialized)
        {
            initialized = false;
            try
            {
                var asm = Assembly.Load("ICSharpCode.Core");
                if (asm == null) return null;
                var t = asm.GetType("ICSharpCode.Core.PropertyService");
                if (t == null) return null;

                var initProp = t.GetProperty("Initialized", BindingFlags.Public | BindingFlags.Static);
                if (initProp != null)
                {
                    object v = initProp.GetValue(null, null);
                    initialized = (v is bool) && (bool)v;
                }

                // Reading ConfigDirectory before initialisation can throw in this fork; don't try.
                if (!initialized) return null;

                var cfgProp = t.GetProperty("ConfigDirectory", BindingFlags.Public | BindingFlags.Static);
                if (cfgProp == null) return null;
                return cfgProp.GetValue(null, null) as string;
            }
            catch { return null; }
        }

        private static string Cache(string value)
        {
            _cached = Normalize(value);
            _isCached = true;
            return _cached;
        }

        /// <summary>Full, separator-trimmed path, or null. Never throws on a malformed argument.</summary>
        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\', '/'); }
            catch { return null; }
        }
    }
}
