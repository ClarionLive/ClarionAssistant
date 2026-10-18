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

        /// <summary>
        /// Pipeline run 4 (security MEDIUM): a NETWORK path is never opened, and so never resolved either. Opening
        /// \\attacker\share\x (or a WebDAV \\host@80\x, or a mapped network drive) makes Windows authenticate to that host
        /// with the developer's NTLM credentials. Such a path gets a lexical-only identity (its normalised text).
        /// </summary>
        public static Probe Of(string path)
        {
            var p = new Probe { Full = FullOrSelf(path) };
            if (string.IsNullOrEmpty(path)) return p;
            if (IsNetworkPath(path)) { p.Network = true; return p; }
            Query(path, out p.HasId, out p.Id, out p.Final);
            p.Network = p.Final != null && IsNetworkPath(p.Final);
            return p;
        }

        /// <summary>
        /// Decided from the path TEXT, plus the drive's type for a drive letter (GetDriveType reads the local mount table,
        /// cached per letter for 30 s; it does not contact any server). Network: anything starting \\ or // (UNC, WebDAV
        /// \\host@port\, \\?\UNC\, and every other device namespace such as \\?\GLOBALROOT) EXCEPT \\?\X: and \\.\X: on a
        /// local drive; and a drive letter Windows reports as remote.
        /// </summary>
        public static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.Replace('/', '\\');
            // Pipeline run 5: the NT object namespace (\??\UNC\host\share, \??\C:\...) is never a plain local path here.
            if (p.StartsWith(@"\??\", StringComparison.Ordinal)) return true;
            // \\.\ paths are normalised by Win32 ('..' climbs past the drive: \\.\C:\..\UNC\host\share is a share), so
            // one with any '..' segment is treated as network rather than reasoned about.
            if (p.StartsWith(@"\\.\", StringComparison.Ordinal) && HasDotDotSegment(p.Substring(4))) return true;
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                string rest = p.Substring(4);
                return !(IsDriveRooted(rest) && !IsRemoteDrive(rest[0]));
            }
            if (p.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            if (IsDriveRooted(p)) return IsRemoteDrive(p[0]);
            return false;   // relative: resolved against the (local) current directory
        }

        private static bool IsDriveRooted(string p) { return p.Length >= 2 && p[1] == ':' && char.IsLetter(p[0]); }

        private static bool HasDotDotSegment(string p)
        {
            foreach (var seg in p.Split('\\')) if (seg.Trim() == "..") return true;
            return false;
        }

        private static readonly object _driveLock = new object();
        private static readonly Dictionary<char, KeyValuePair<bool, int>> _driveCache = new Dictionary<char, KeyValuePair<bool, int>>();

        private static bool IsRemoteDrive(char letter)
        {
            char d = char.ToUpperInvariant(letter);
            lock (_driveLock)
            {
                KeyValuePair<bool, int> e;
                if (_driveCache.TryGetValue(d, out e) && unchecked(Environment.TickCount - e.Value) < 30000) return e.Key;
            }
            bool remote;
            try { remote = GetDriveTypeW(d + @":\") == DRIVE_REMOTE; } catch { remote = false; }
            lock (_driveLock) _driveCache[d] = new KeyValuePair<bool, int>(remote, Environment.TickCount);
            return remote;
        }

        /// <summary>
        /// Pipeline run 4: pure-string normalisation for comparing paths that must not be touched (a refused or a network
        /// path): / to \, doubled separators collapsed, '.' dropped, '..' pops a segment but never climbs above the root.
        /// The root (X:\, \\?\X:\, \\server\share\, \\?\UNC\server\share\) is kept as written. No I/O.
        /// </summary>
        public static string NormalizeText(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string p = path.Replace('/', '\\');
            string root;
            int rootLen;
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) rootLen = RootAfterServerShare(p, 8);
            else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
                rootLen = IsDriveRooted(p.Substring(4)) ? Math.Min(p.Length, 7) : Math.Min(p.Length, 4);
            else if (p.StartsWith(@"\\", StringComparison.Ordinal)) rootLen = RootAfterServerShare(p, 2);
            else if (IsDriveRooted(p)) rootLen = p.Length >= 3 && p[2] == '\\' ? 3 : 2;
            else rootLen = 0;
            root = p.Substring(0, rootLen);
            if (root.Length > 0 && !root.EndsWith("\\") && rootLen < p.Length) root += "\\";
            var parts = new List<string>();
            foreach (var seg in p.Substring(rootLen).Split('\\'))
            {
                if (seg.Length == 0 || seg == ".") continue;
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(seg);
            }
            return root + string.Join("\\", parts.ToArray());
        }

        // \\server\share\ : the root ends after the share's separator (or at the end).
        private static int RootAfterServerShare(string p, int start)
        {
            int i = p.IndexOf('\\', start);          // end of server
            if (i < 0) return p.Length;
            int j = p.IndexOf('\\', i + 1);          // end of share
            return j < 0 ? p.Length : j + 1;
        }

        /// <summary>Test seam (pipeline run 4): the one place a file handle is opened. A harness records every path it is
        /// asked to open, to prove a network path never reaches it. Production: CreateFileW for attributes only.</summary>
        public static Func<string, IntPtr> Opener = path =>
            CreateFileW(path, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

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
            // Pipeline runs 2 and 4: a tab on a network path (UNC, WebDAV, a mapped drive) is NEVER opened: a disconnected
            // share would stall for the SMB timeout, and opening an attacker's share leaks the developer's NTLM
            // credentials. It matches by its text only (above).
            if (IsNetworkPath(candidate)) return false;
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

        /// <summary>Does the tab path name any of <paramref name="wanted"/> as TEXT (both normalised with
        /// <see cref="NormalizeText"/>)? No I/O at all: for paths that must not be touched.</summary>
        public static bool LexicalMatch(string tabPath, IList<string> wanted)
        {
            if (string.IsNullOrEmpty(tabPath) || wanted == null) return false;
            string t = NormalizeText(tabPath);
            foreach (var w in wanted)
                if (!string.IsNullOrEmpty(w) && Lexical(t, NormalizeText(w))) return true;
            return false;
        }

        public static bool Lexical(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private static string FullOrSelf(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            // Not GetFullPath for a network path: it can call GetLongPathName on it (8.3 expansion), which is network I/O.
            if (IsNetworkPath(path)) return NormalizeText(path);
            try { return Path.GetFullPath(path); } catch { return NormalizeText(path); }
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
            // Pipeline runs 4-5: never opened (NTLM leak). The callers already check; this is the floor, and it holds for
            // every spelling Win32 would route to a share: UNC, \\?\UNC\, \??\..., \\.\ with '..', a remote drive letter.
            if (IsNetworkPath(path)) return;
            IntPtr h;
            try { var open = Opener; h = open != null ? open(path) : IntPtr.Zero; }
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
