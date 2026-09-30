using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// 7116020b: Clarion.exe is 32-bit and not LargeAddressAware, so it has 2 GB of address space, and what
    /// kills it is the LARGEST FREE BLOCK of that space, not the working set. On 2026-09-30 it died with an
    /// OutOfMemoryException inside Clarion's own OpenPwee at 975 MB working set. The live repro that followed
    /// measured the largest free block at 15-81 MB around each open of a 3.2 MB procedure.
    ///
    /// Two parts: Measure() walks the region list with VirtualQuery, a few thousand calls, a few ms. And a
    /// watcher that warns ONCE, with a non-modal notice, when the largest free block stays low, then re-arms
    /// after it recovers. It can't prevent an OOM in Clarion's native code (that happens before CA is
    /// involved), but it turns a sudden crash into "save and restart soon".
    /// </summary>
    internal static class MemoryHeadroom
    {
        internal struct AddressSpace
        {
            public bool Ok;
            public long FreeMB;
            public long LargestFreeMB;
            public long UserSpaceMB;   // ~2048 normally, ~4096 when Clarion.exe is LargeAddressAware
            public long ElapsedMs;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public UIntPtr RegionSize;   // sequential layout pads before this on x64, matching the Win32 struct
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll")]
        private static extern UIntPtr VirtualQuery(IntPtr lpAddress, out MemoryBasicInformation lpBuffer, UIntPtr dwLength);

        private const uint MemFree = 0x10000;

        /// <summary>Free and largest-free address space of THIS process, in MB. Never throws; Ok=false on failure.</summary>
        internal static AddressSpace Measure()
        {
            var result = new AddressSpace();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // 83374953: with Clarion.exe patched LargeAddressAware, a 32-bit process has up to 4 GB, and its
                // addresses above 2 GB are NEGATIVE as a 32-bit IntPtr. So in a 32-bit process, carry addresses
                // as unsigned 32-bit values: build the IntPtr from the low 32 bits, and read BaseAddress back
                // with ToInt32 cast through uint (ToInt64 would sign-extend). The walk ends when VirtualQuery
                // says the address is past user space, which is 2 GB or 4 GB depending on the flag.
                bool is32 = IntPtr.Size == 4;
                long limit = is32 ? 0x100000000L : long.MaxValue;
                long free = 0, largest = 0, addr = 0, top = 0;
                var size = new UIntPtr((uint)Marshal.SizeOf(typeof(MemoryBasicInformation)));
                for (int guard = 0; guard < 200000 && addr < limit; guard++)
                {
                    MemoryBasicInformation mbi;
                    var p = is32 ? new IntPtr(unchecked((int)(uint)addr)) : new IntPtr(addr);
                    if (VirtualQuery(p, out mbi, size) == UIntPtr.Zero) break;
                    long region = (long)mbi.RegionSize.ToUInt64();
                    if (region <= 0) break;
                    long bas = is32 ? (long)(uint)mbi.BaseAddress.ToInt32() : mbi.BaseAddress.ToInt64();
                    if (mbi.State == MemFree) { free += region; if (region > largest) largest = region; }
                    long next = bas + region;
                    if (next <= addr) break;
                    addr = next;
                    top = next;
                }
                result.FreeMB = free >> 20;
                result.LargestFreeMB = largest >> 20;
                result.UserSpaceMB = top >> 20;
                result.Ok = largest > 0;
            }
            catch { result.Ok = false; }
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }

        // ── Watcher ─────────────────────────────────────────────────────────────────────────────────────
        // Thresholds come from the 2026-09-30 repro on a 3.2 MB procedure: an open dips the largest free block
        // by roughly 20-65 MB. So a steady state under 48 MB has no margin left for the next big open. Re-arm
        // well above that so a value hovering at the line doesn't flap.
        // Both are one repro's worth of evidence, so the warn line can be overridden without a rebuild: put a
        // number of MB in %LOCALAPPDATA%\ClarionAssistant\mem-watch-warn-mb.txt (read at startup). Re-arm is
        // always twice the warn line. A high value is also how to see the notice without starving Clarion.
        internal const long DefaultWarnBelowMB = 48;
        internal const string OverrideFileName = "mem-watch-warn-mb.txt";
        private static long WarnBelowMB = DefaultWarnBelowMB;
        private static long RearmAboveMB = DefaultWarnBelowMB * 2;
        private const int IntervalMs = 15000;
        private const string NoticeKey = "low-memory";

        private static Timer _timer;
        private static bool _warned;

        /// <summary>The override from <see cref="OverrideFileName"/>, or null when absent or not a sane number.</summary>
        internal static long? ReadOverride(string dataDir)
        {
            try
            {
                string p = System.IO.Path.Combine(dataDir, OverrideFileName);
                if (!System.IO.File.Exists(p)) return null;
                long mb;
                if (long.TryParse(System.IO.File.ReadAllText(p).Trim(), out mb) && mb >= 1 && mb <= 2048) return mb;
            }
            catch { }
            return null;
        }

        /// <summary>Start from /Workspace/Autostart (UI thread). Only meaningful in a 32-bit process. Never throws.</summary>
        internal static void Start()
        {
            try
            {
                if (IntPtr.Size != 4 || _timer != null) return;
                long? o = ReadOverride(MonacoSpikeLog.DataDir);
                if (o.HasValue) { WarnBelowMB = o.Value; RearmAboveMB = o.Value * 2; }
                _timer = new Timer { Interval = IntervalMs };
                _timer.Tick += (s, e) => Check();
                _timer.Start();
                MonacoSpikeLog.Write("[mem-watch] started warnBelow=" + WarnBelowMB + "MB rearmAbove=" + RearmAboveMB + "MB"
                    + (o.HasValue ? " (override from " + OverrideFileName + ")" : "") + " " + MonacoSpikeLog.MemSummary());
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[mem-watch] start failed: " + ex.Message); }
        }

        internal static void Stop()
        {
            try { if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; } } catch { }
        }

        // ── Per-phase allocation markers (1d8d1c49) ─────────────────────────────────────────────────────
        // AppDomain resource monitoring counts every byte allocated, whether or not a GC ran in between,
        // which GC.GetTotalMemory can't. Enabled once (it can't be turned off); overhead is small.
        // Deltas are domain-wide, so a phase is only clean if nothing else allocates concurrently: the
        // UI-thread phases of an open are; the lane phases are logged with their thread id.

        private static bool _monitoringOn;

        private static long AllocatedBytes()
        {
            try
            {
                if (!_monitoringOn) { AppDomain.MonitoringIsEnabled = true; _monitoringOn = true; }
                return AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            }
            catch { return -1; }
        }

        private sealed class PhaseScope : IDisposable
        {
            private readonly string _tag;
            private readonly long _alloc0;
            private readonly int _gen2;
            private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
            internal PhaseScope(string tag) { _tag = tag; _gen2 = GC.CollectionCount(2); _alloc0 = AllocatedBytes(); }
            public void Dispose()
            {
                try
                {
                    long a1 = AllocatedBytes();
                    MonacoSpikeLog.Write("[mem-phase] " + _tag
                        + " alloc=" + (_alloc0 < 0 || a1 < 0 ? "?" : ((a1 - _alloc0) / 1048576.0).ToString("0.0")) + "MB"
                        + " gc=" + (GC.GetTotalMemory(false) >> 20) + "MB gen2+=" + (GC.CollectionCount(2) - _gen2)
                        + " ms=" + _sw.ElapsedMilliseconds + " thread=" + System.Threading.Thread.CurrentThread.ManagedThreadId);
                }
                catch { }
            }
        }

        /// <summary>Log how much the enclosed block allocated: <c>using (MemoryHeadroom.Phase("tag")) { ... }</c>.</summary>
        internal static IDisposable Phase(string tag) { return new PhaseScope(tag); }

        private static Timer _settleTimer;
        private static string _settleTag;

        /// <summary>A few seconds after an open, force a full collection and log what is actually RETAINED plus the
        /// largest free block: the peak-vs-kept split for the phases above. Debounced; UI thread.</summary>
        internal static void MarkSettledSoon(string tag)
        {
            try
            {
                _settleTag = tag;
                if (_settleTimer == null)
                {
                    _settleTimer = new Timer { Interval = 4000 };
                    _settleTimer.Tick += (s, e) =>
                    {
                        _settleTimer.Stop();
                        long total = AllocatedBytes();
                        long retained = GC.GetTotalMemory(true);
                        var a = Measure();
                        MonacoSpikeLog.Write("[mem-phase] SETTLED " + _settleTag + " retainedGc=" + (retained >> 20) + "MB"
                            + " allocTotal=" + (total >> 20) + "MB largestFree=" + a.LargestFreeMB + "MB space=" + a.UserSpaceMB + "MB");
                    };
                }
                _settleTimer.Stop();
                _settleTimer.Start();
            }
            catch { }
        }

        // ── Compact after a big editor closes ───────────────────────────────────────────────────────────
        // The 2026-09-30 A/B: the CA Embeditor on a 3.2 MB procedure cost ~136 MB more than Clarion's own and
        // gave NONE of it back on close (largest free block stuck at 39 MB). A multi-MB buffer lives as 6+ MB
        // strings in the large-object heap, which .NET never compacts unless asked, so the freed space stays
        // in pieces too small for the next big open. One compacting collection after a big close returns it.
        // Gated so closing small embeds never pays for a full collection, and debounced so a close that tears
        // down several things collects once.
        internal const int CompactMinChars = 500000;
        private const int CompactDelayMs = 1500;   // let the WebView2 disposal and the native close settle first
        private static Timer _compactTimer;
        private static string _compactWho;

        /// <summary>Schedule one compacting GC after an editor holding <paramref name="bufferChars"/> closes.
        /// Runs on the UI thread from a timer, never on the close stack. Never throws.</summary>
        internal static void CompactAfterClose(string who, long bufferChars)
        {
            try
            {
                if (IntPtr.Size != 4 || bufferChars < CompactMinChars) return;
                _compactWho = who + " chars=" + bufferChars;
                if (_compactTimer == null)
                {
                    _compactTimer = new Timer { Interval = CompactDelayMs };
                    _compactTimer.Tick += (s, e) => { _compactTimer.Stop(); RunCompaction(); };
                }
                _compactTimer.Stop();
                _compactTimer.Start();
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[mem-compact] schedule failed: " + ex.Message); }
        }

        // 83374953: on a LargeAddressAware Clarion the largest free block stayed at 1,527 MB through a 3.2 MB
        // open and close, so the compaction pause (259-568 ms) bought nothing. Skip it when there is room.
        internal const long CompactOnlyBelowLargestFreeMB = 512;

        private static void RunCompaction()
        {
            try
            {
                var pre = Measure();
                if (pre.Ok && pre.LargestFreeMB >= CompactOnlyBelowLargestFreeMB)
                {
                    MonacoSpikeLog.Write("[mem-compact] skipped after " + _compactWho + " largestFree=" + pre.LargestFreeMB
                        + "MB >= " + CompactOnlyBelowLargestFreeMB + "MB space=" + pre.UserSpaceMB + "MB");
                    return;
                }
                string before = MonacoSpikeLog.MemSummary();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long ms = sw.ElapsedMilliseconds;
                MonacoSpikeLog.Write("[mem-compact] after " + _compactWho + " ms=" + ms + " before: " + before + " after: " + MonacoSpikeLog.MemSummary());
                if (_warned) Check();   // headroom may be back: re-arm/dismiss the warning now, not in 15 s
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[mem-compact] failed: " + ex.Message); }
        }

        private static void Check()
        {
            try
            {
                var a = Measure();
                if (!a.Ok) return;
                if (!_warned && a.LargestFreeMB < WarnBelowMB)
                {
                    _warned = true;
                    MonacoSpikeLog.Write("[mem-watch] LOW — warning shown " + MonacoSpikeLog.MemSummary() + " scanMs=" + a.ElapsedMs);
                    Terminal.CaNotice.Post(NoticeKey, "Clarion is running low on memory",
                        "Clarion has only " + a.LargestFreeMB + " MB of memory left in one piece. Opening a large procedure "
                        + "or file may crash the IDE. Save your work and restart Clarion soon.");
                }
                else if (_warned && a.LargestFreeMB > RearmAboveMB)
                {
                    _warned = false;
                    MonacoSpikeLog.Write("[mem-watch] recovered " + MonacoSpikeLog.MemSummary());
                    Terminal.CaNotice.Dismiss(NoticeKey);
                }
            }
            catch (Exception ex) { MonacoSpikeLog.Write("[mem-watch] check failed: " + ex.Message); }
        }
    }
}
