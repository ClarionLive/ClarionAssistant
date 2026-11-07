using System;
using System.IO;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Which processes are the Clarion IDE (ticket 1976d2e9). SoftVelocity ships a signed ClarionLAA.exe -
    /// Clarion.exe built LargeAddressAware, so it gets 4 GB instead of 2 on 64-bit Windows - beside the
    /// unchanged Clarion.exe, and the developer picks one per session. Every check that asks "is this the
    /// IDE?" goes through here, so the two exes are always treated alike. They used to disagree: one check
    /// wanted exactly "Clarion", another anything starting with "Clarion" (which ClarionClone.IDE also does).
    /// No IDE coupling: tests\ClarionIdeProcess.Test.cs compiles this file as it is.
    /// </summary>
    public static class ClarionIdeProcess
    {
        /// <summary>The IDE's process image names (Process.ProcessName: the exe name without ".exe").</summary>
        public static readonly string[] IdeImageNames = { "Clarion", "ClarionLAA" };

        /// <summary>The LargeAddressAware build's file name, in the same bin folder as Clarion.exe.</summary>
        public const string LaaExeName = "ClarionLAA.exe";

        /// <summary>True for a process name that is the Clarion IDE: exactly one of <see cref="IdeImageNames"/>, any case.</summary>
        public static bool IsIdeImageName(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return false;
            foreach (var n in IdeImageNames)
                if (string.Equals(processName.Trim(), n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>True for a path whose file is the Clarion IDE's exe (Clarion.exe or ClarionLAA.exe).</summary>
        public static bool IsIdeExe(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            string file;
            try { file = Path.GetFileName(exePath); } catch { return false; }
            if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            return IsIdeImageName(file.Substring(0, file.Length - 4));
        }

        /// <summary>True for the LargeAddressAware build (ClarionLAA.exe).</summary>
        public static bool IsLaaExe(string exePath)
        {
            try { return string.Equals(Path.GetFileName(exePath ?? ""), LaaExeName, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>
        /// The extra sentence for the low-memory notice, or null. When the IDE is the standard Clarion.exe and
        /// ClarionLAA.exe sits beside it, the developer can get twice the memory just by starting that one
        /// instead; the notice says so. Nothing to add when they already run ClarionLAA.exe, or have none.
        /// <paramref name="fileExists"/> is File.Exists in production (a seam for the test).
        /// </summary>
        public static string LowMemoryAdvice(string hostExePath, Func<string, bool> fileExists)
        {
            if (!IsIdeExe(hostExePath) || IsLaaExe(hostExePath)) return null;
            string laa;
            try { laa = Path.Combine(Path.GetDirectoryName(hostExePath), LaaExeName); } catch { return null; }
            bool exists;
            try { exists = (fileExists ?? File.Exists)(laa); } catch { exists = false; }
            return exists
                ? "Next time, start Clarion with " + LaaExeName + " (in the same folder as Clarion.exe): it gives Clarion twice the memory."
                : null;
        }
    }
}
