using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// GH #205 pipeline run 1: "is this the same file?" by FILE IDENTITY, not by path text. The write tools' path goes
    /// through the #266 gate, which resolves links (GetFinalPathNameByHandle); a tab may hold the same file under a
    /// junction, a subst drive, a symlink, an 8.3 name or a hard link. Comparing path text read such a tab as "not
    /// open", and the write went underneath its unsaved changes: the #205 data loss again.
    ///
    /// <see cref="SameFile"/>, in order:
    ///   1. the full paths are equal (no I/O; the common case);
    ///   2. both files exist and their volume serial + file index are equal (GetFileInformationByHandle: collapses
    ///      every alias, hard links included). A zero file index (some network/virtual file systems) is not an identity;
    ///   3. both resolve (GetFinalPathNameByHandle) to the same final path;
    ///   else different.
    /// Handles are opened for attributes only (access 0, share all), so a file the user cannot read, or one another
    /// process holds open, still answers. IDE-free; never throws.
    /// </summary>
    public static class FileIdentity
    {
        public struct Id
        {
            public uint Volume;
            public ulong Index;
            public bool Equals(Id o) { return Volume == o.Volume && Index == o.Index; }
            public override string ToString() { return Volume.ToString("x8") + ":" + Index.ToString("x16"); }
        }

        /// <summary>A path with its identity and final path looked up once, for comparing against many tabs.</summary>
        public sealed class Probe
        {
            public string Full;
            public bool HasId;
            public Id Id;
            public string Final;
            /// <summary>The target is on the network (as spelled, or once resolved).</summary>
            public bool Network;
        }

        public static Probe Of(string path)
        {
            var p = new Probe { Full = FullOrSelf(path) };
            if (string.IsNullOrEmpty(path)) return p;
            Query(path, out p.HasId, out p.Id, out p.Final);
            p.Network = IsNetworkPath(path) || (p.Final != null && IsNetworkPath(p.Final));
            return p;
        }

        /// <summary>UNC (\\server\share, \\?\UNC\...) or a drive letter Windows reports as remote. No network I/O:
        /// GetDriveType reads the drive's mapping, it does not contact the server.</summary>
        public static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                return IsNetworkPath(path.Substring(4));
            if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return true;
            if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
            {
                try { return GetDriveTypeW(path.Substring(0, 2) + "\\") == DRIVE_REMOTE; } catch { return false; }
            }
            return false;
        }

        private const uint DRIVE_REMOTE = 4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetDriveTypeW(string root);

        public static bool SameFile(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return Same(Of(a), b);
        }

        /// <summary>Does <paramref name="candidate"/> name the same file as any of <paramref name="probes"/>?</summary>
        public static bool MatchesAny(string candidate, IList<Probe> probes)
        {
            if (string.IsNullOrEmpty(candidate) || probes == null) return false;
            string full = FullOrSelf(candidate);
            foreach (var p in probes)
                if (p != null && Lexical(p.Full, full)) return true;
            // Pipeline run 2: a tab on a network path (UNC, a mapped drive) is not opened to learn its identity unless the
            // target itself is on the network: a disconnected share would stall for the SMB timeout, and a local target
            // cannot be the same file as a remote tab anyway.
            if (IsNetworkPath(candidate))
            {
                bool anyNetwork = false;
                foreach (var p in probes) if (p != null && p.Network) anyNetwork = true;
                if (!anyNetwork) return false;
            }
            bool hasId; Id id; string final;
            Query(candidate, out hasId, out id, out final);
            foreach (var p in probes)
            {
                if (p == null) continue;
                if (hasId && p.HasId && id.Equals(p.Id)) return true;
                if (final != null && p.Final != null && Lexical(final, p.Final)) return true;
            }
            return false;
        }

        private static bool Same(Probe a, string b)
        {
            return MatchesAny(b, new[] { a });
        }

        public static bool Lexical(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static string FullOrSelf(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            try { return Path.GetFullPath(path); } catch { return path; }
        }

        private const uint FILE_SHARE_ALL = 0x7;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(IntPtr h, out BY_HANDLE_FILE_INFORMATION info);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(IntPtr h, StringBuilder buf, uint len, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        private static void Query(string path, out bool hasId, out Id id, out string final)
        {
            hasId = false; id = new Id(); final = null;
            IntPtr h;
            try { h = CreateFileW(path, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero); }
            catch { return; }
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return;
            try
            {
                BY_HANDLE_FILE_INFORMATION info;
                if (GetFileInformationByHandle(h, out info) && (info.FileIndexHigh != 0 || info.FileIndexLow != 0))
                {
                    hasId = true;
                    id = new Id { Volume = info.VolumeSerialNumber, Index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow };
                }
                var sb = new StringBuilder(1024);
                uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
                if (n >= sb.Capacity)
                {
                    sb = new StringBuilder((int)n + 1);
                    n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
                }
                if (n > 0 && n < sb.Capacity)
                {
                    string s = sb.ToString();
                    if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) s = @"\\" + s.Substring(8);
                    else if (s.StartsWith(@"\\?\")) s = s.Substring(4);
                    final = s;
                }
            }
            catch { }
            finally { CloseHandle(h); }
        }
    }
}
