using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ClarionAssistant.Services;

// Harness for FileIdentity (GH #205 pipeline run 1): the open-tab check must find a tab that holds the target file
// under ANOTHER spelling. The write tools' path is link-resolved by the #266 gate; the tab may have been opened through
// a junction, a subst drive, a symlink, an 8.3 short name or a hard link. Comparing path text read those tabs as
// "not open" and the write went under their unsaved changes.
//
// Every alias here is REAL (mklink /J, subst, CreateHardLink, CreateSymbolicLink, the volume's own 8.3 name), and each
// case first checks the two spellings really differ as text, so a pass cannot come from the lexical shortcut. An
// alias this machine cannot make (no free drive letter for subst; symlinks need Developer Mode or admin; 8.3 names
// disabled on the volume) is reported as SKIPPED, never as a pass; the junction and hard-link cases always run.
//
// Run:  tests\Run-Tests.ps1
//
// This file is NOT in ClarionAssistant.csproj and must never be added to it — it has its own Main().
static class FileIdentityTest
{
    static int pass = 0, fail = 0, skipped = 0;

    static void Ok(string name, bool cond, string detail = null)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  (" + detail + ")" : "")); }
    }

    static void Skip(string name, string why) { skipped++; Console.WriteLine("  [skip] " + name + " - " + why); }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLinkW(string link, string existing, IntPtr sa);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateSymbolicLinkW(string link, string target, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint len);

    static int Cmd(string args)
    {
        var psi = new ProcessStartInfo("cmd.exe", "/c " + args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = Process.Start(psi)) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); return p.ExitCode; }
    }

    // An alias must REALLY be the same file and REALLY be spelled differently.
    static void Alias(string label, string real, string alias)
    {
        Ok(label + ": the spellings differ as text", !FileIdentity.Lexical(real, alias), alias);
        Ok(label + ": SameFile", FileIdentity.SameFile(real, alias));
        Ok(label + ": SameFile, the other way round", FileIdentity.SameFile(alias, real));
        Ok(label + ": a tab on the alias matches a probe of the real path",
           FileIdentity.MatchesAny(alias, new List<FileIdentity.Probe> { FileIdentity.Of(real) }));
        Ok(label + ": a tab on the real path matches a probe of the alias",
           FileIdentity.MatchesAny(real, new List<FileIdentity.Probe> { FileIdentity.Of(alias) }));
    }

    static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ca-fileid-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        string real = Path.Combine(root, "src", "LongModuleName001.clw");
        string other = Path.Combine(root, "src", "Other.clw");
        string twin = Path.Combine(root, "elsewhere", "LongModuleName001.clw");
        string junction = Path.Combine(root, "via-junction");
        string substDrive = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(real));
            Directory.CreateDirectory(Path.GetDirectoryName(twin));
            File.WriteAllText(real, "  MEMBER()\r\n");
            File.WriteAllText(other, "  MEMBER()\r\n");
            File.WriteAllText(twin, "  MEMBER()\r\n");

            Console.WriteLine("-- plain cases");
            Ok("same path", FileIdentity.SameFile(real, real));
            Ok("same path, other case and a trailing dot segment", FileIdentity.SameFile(real, Path.Combine(root, "SRC", ".", "longmodulename001.CLW")));
            Ok("a different file in the same folder is NOT the same", !FileIdentity.SameFile(real, other));
            Ok("the same NAME in another folder is NOT the same", !FileIdentity.SameFile(real, twin));
            Ok("a missing file is not the same as an existing one", !FileIdentity.SameFile(real, Path.Combine(root, "src", "Missing.clw")));
            Ok("empty / null never match", !FileIdentity.SameFile(real, null) && !FileIdentity.SameFile("", real)
               && !FileIdentity.MatchesAny(null, new List<FileIdentity.Probe> { FileIdentity.Of(real) }));

            Console.WriteLine("-- junction (mklink /J)");
            if (Cmd("mklink /J \"" + junction + "\" \"" + Path.Combine(root, "src") + "\"") == 0 && Directory.Exists(junction))
            {
                Alias("junction", real, Path.Combine(junction, "LongModuleName001.clw"));
                Ok("junction: a different file through the junction is NOT the same", !FileIdentity.SameFile(real, Path.Combine(junction, "Other.clw")));
            }
            else Ok("could create a junction", false, "mklink /J failed");

            Console.WriteLine("-- hard link");
            string hard = Path.Combine(root, "elsewhere", "HardLinked.clw");
            if (CreateHardLinkW(hard, real, IntPtr.Zero)) Alias("hard link", real, hard);
            else Ok("could create a hard link", false, "CreateHardLink error " + Marshal.GetLastWin32Error());

            Console.WriteLine("-- subst drive");
            for (char c = 'Z'; c >= 'G' && substDrive == null; c--)
            {
                string d = c + ":";
                if (Directory.Exists(d + "\\")) continue;
                if (Cmd("subst " + d + " \"" + Path.Combine(root, "src") + "\"") == 0 && File.Exists(d + "\\LongModuleName001.clw")) substDrive = d;
            }
            if (substDrive != null) Alias("subst " + substDrive, real, substDrive + "\\LongModuleName001.clw");
            else Skip("subst drive", "no free drive letter G-Z could be substituted");

            Console.WriteLine("-- symbolic link");
            string sym = Path.Combine(root, "elsewhere", "Symlinked.clw");
            if (CreateSymbolicLinkW(sym, real, 0x2 /* ALLOW_UNPRIVILEGED_CREATE */) && File.Exists(sym)) Alias("symlink", real, sym);
            else Skip("symbolic link", "not creatable here (needs Developer Mode or admin), error " + Marshal.GetLastWin32Error());

            Console.WriteLine("-- 8.3 short name");
            var sb = new StringBuilder(1024);
            uint n = GetShortPathNameW(real, sb, (uint)sb.Capacity);
            string shortName = n > 0 ? sb.ToString() : null;
            if (shortName != null && !FileIdentity.Lexical(shortName, real)) Alias("8.3 name", real, shortName);
            else Skip("8.3 short name", "the volume does not generate short names");
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine("  [FAIL] harness threw: " + ex);
        }
        finally
        {
            if (substDrive != null) Cmd("subst " + substDrive + " /D");
            try { if (Directory.Exists(junction)) Directory.Delete(junction); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine("FileIdentity: " + pass + " passed, " + fail + " failed, " + skipped + " skipped");
        return fail == 0 ? 0 : 1;
    }
}
