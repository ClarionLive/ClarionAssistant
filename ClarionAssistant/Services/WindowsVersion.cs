using System;
using Microsoft.Win32;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// The real Windows build number, and the minimum Clarion Assistant's terminals can run on
    /// (GitHub #236).
    ///
    /// NOT Environment.OSVersion: under .NET Framework it reports what the process manifest
    /// declares support for, not the OS, so inside clarion.exe it can say 6.2 (Windows 8) on
    /// Windows 11. The registry's CurrentBuildNumber is what winver shows.
    /// </summary>
    public static class WindowsVersion
    {
        /// <summary>
        /// Windows 10 1809 / Windows Server 2019. CreatePseudoConsole (ConPTY), which every
        /// assistant tab runs on, first shipped in this build, and Claude Code documents the same
        /// floor.
        ///
        /// CLARIONASSISTANT_MIN_WINDOWS_BUILD can RAISE it, never lower it, so the refusal path
        /// can be exercised on a supported machine without the variable ever becoming a way to
        /// push an unsupported one into ConPTY (Codex adversary, pipeline run 1). Logged when it
        /// applies, because a leftover value would otherwise block launches with no visible cause.
        /// </summary>
        public static int MinimumSupportedBuild
        {
            get
            {
                int overrideBuild;
                string v = Environment.GetEnvironmentVariable("CLARIONASSISTANT_MIN_WINDOWS_BUILD");
                if (!string.IsNullOrEmpty(v) && int.TryParse(v.Trim(), out overrideBuild) && overrideBuild > Floor)
                {
                    System.Diagnostics.Debug.WriteLine("[WindowsVersion] minimum raised to " + overrideBuild
                        + " by CLARIONASSISTANT_MIN_WINDOWS_BUILD");
                    return overrideBuild;
                }
                return Floor;
            }
        }

        private const int Floor = 17763;

        /// <summary>
        /// The OS build number (e.g. 14393 for Server 2016, 26100 for Windows 11 24H2), or 0
        /// when it cannot be read. Callers treat 0 as "unknown - do not block": refusing to start
        /// on a machine we merely failed to identify would be worse than the old failure.
        /// </summary>
        public static int GetBuildNumber()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    object raw = key?.GetValue("CurrentBuildNumber") ?? key?.GetValue("CurrentBuild");
                    int build;
                    if (raw != null && int.TryParse(raw.ToString().Trim(), out build) && build > 0)
                        return build;
                }
            }
            catch { }
            return 0;
        }
    }
}
