using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClarionAssistant.Terminal
{
    public class ConPtyTerminal : IDisposable
    {
        private IntPtr _pseudoConsoleHandle;
        private IntPtr _processHandle;
        private IntPtr _threadHandle;
        private IntPtr _attributeList;
        private IntPtr _jobHandle;

        private SafeFileHandle _inputReadSide, _inputWriteSide;
        private SafeFileHandle _outputReadSide, _outputWriteSide;

        private FileStream _inputWriter;
        private FileStream _outputReader;
        private Thread _readThread;
        private Thread _processWaitThread;

        private bool _isDisposed;
        private bool _isRunning;

        // Static registry of LIVE terminals so the addin shutdown hook can fast-kill every child-process
        // tree (pwsh -> claude/node -> conhost) BEFORE native IDE teardown — the prime cause of Clarion
        // hanging on close. See ShutdownService.Terminate() / KillAllForShutdown().
        private static readonly object _liveLock = new object();
        private static readonly List<ConPtyTerminal> _live = new List<ConPtyTerminal>();

        private readonly ConcurrentQueue<byte[]> _outputQueue = new ConcurrentQueue<byte[]>();
        private Timer _outputTimer;
        private const int OUTPUT_INTERVAL_MS = 16;
        private byte[] _utf8Remainder = new byte[0];

        private int _cols, _rows;

        public event Action<byte[]> DataReceived;
        public event EventHandler ProcessExited;

        public bool IsRunning { get { return _isRunning && !_isDisposed; } }
        public int Columns { get { return _cols; } }
        public int Rows { get { return _rows; } }

        public void Start(int cols, int rows, string command, string workingDirectory = null)
        {
            if (_isRunning)
                throw new InvalidOperationException("Terminal is already running");

            _cols = cols;
            _rows = rows;

            if (string.IsNullOrEmpty(workingDirectory))
                workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            try
            {
                CreatePipes();
                CreatePseudoConsole(cols, rows);
                StartProcess(command, workingDirectory);

                _inputReadSide.Dispose();
                _inputReadSide = null;
                _outputWriteSide.Dispose();
                _outputWriteSide = null;

                _isRunning = true;
                lock (_liveLock) { if (!_live.Contains(this)) _live.Add(this); }
                _outputTimer = new Timer(FlushOutputQueue, null, 0, OUTPUT_INTERVAL_MS);
                StartReading();
                StartProcessWait();
            }
            catch
            {
                Cleanup();
                throw;
            }
        }

        public void Write(byte[] data)
        {
            if (!_isRunning || _inputWriter == null) return;
            try
            {
                _inputWriter.Write(data, 0, data.Length);
                _inputWriter.Flush();
            }
            catch (IOException) { }
        }

        public void Write(string text)
        {
            Write(Encoding.UTF8.GetBytes(text));
        }

        public void SendCtrlC()
        {
            Write(new byte[] { 0x03 });
        }

        public void Resize(int cols, int rows)
        {
            if (!_isRunning || _pseudoConsoleHandle == IntPtr.Zero) return;
            _cols = cols;
            _rows = rows;
            NativeMethods.ResizePseudoConsole(_pseudoConsoleHandle, new NativeMethods.COORD((short)cols, (short)rows));
        }

        private void CreatePipes()
        {
            var security = new NativeMethods.SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf(typeof(NativeMethods.SECURITY_ATTRIBUTES)),
                bInheritHandle = true
            };

            if (!NativeMethods.CreatePipe(out _inputReadSide, out _inputWriteSide, ref security, 0))
                throw new InvalidOperationException("Failed to create input pipe");
            if (!NativeMethods.CreatePipe(out _outputReadSide, out _outputWriteSide, ref security, 0))
                throw new InvalidOperationException("Failed to create output pipe");

            NativeMethods.SetHandleInformation(_inputWriteSide, NativeMethods.HANDLE_FLAG_INHERIT, 0);
            NativeMethods.SetHandleInformation(_outputReadSide, NativeMethods.HANDLE_FLAG_INHERIT, 0);
        }

        private void CreatePseudoConsole(int cols, int rows)
        {
            int result = NativeMethods.CreatePseudoConsole(
                new NativeMethods.COORD((short)cols, (short)rows),
                _inputReadSide, _outputWriteSide, 0, out _pseudoConsoleHandle);
            if (result != 0)
                throw new InvalidOperationException("Failed to create pseudo console. Error: " + result);
        }

        private void StartProcess(string commandLine, string workingDirectory)
        {
            IntPtr attrListSize = IntPtr.Zero;
            NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
            _attributeList = Marshal.AllocHGlobal(attrListSize);

            if (!NativeMethods.InitializeProcThreadAttributeList(_attributeList, 1, 0, ref attrListSize))
                throw new InvalidOperationException("Failed to initialize attribute list");

            if (!NativeMethods.UpdateProcThreadAttribute(_attributeList, 0,
                NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _pseudoConsoleHandle,
                (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException("Failed to update attribute list");

            var startupInfo = new NativeMethods.STARTUPINFOEX
            {
                StartupInfo = new NativeMethods.STARTUPINFO
                {
                    cb = Marshal.SizeOf(typeof(NativeMethods.STARTUPINFOEX))
                },
                lpAttributeList = _attributeList
            };

            if (!NativeMethods.CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                NativeMethods.EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, workingDirectory,
                ref startupInfo, out var processInfo))
                throw new InvalidOperationException("Failed to create process: " + Marshal.GetLastWin32Error());

            _processHandle = processInfo.hProcess;
            _threadHandle = processInfo.hThread;

            CreateJobAndAssignProcess();

            _inputWriter = new FileStream(_inputWriteSide, FileAccess.Write);
            _outputReader = new FileStream(_outputReadSide, FileAccess.Read);
        }

        private void CreateJobAndAssignProcess()
        {
            try
            {
                _jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
                if (_jobHandle == IntPtr.Zero) return;

                var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };

                int infoSize = Marshal.SizeOf(typeof(NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr infoPtr = Marshal.AllocHGlobal(infoSize);
                try
                {
                    Marshal.StructureToPtr(info, infoPtr, false);
                    NativeMethods.SetInformationJobObject(_jobHandle, NativeMethods.JobObjectExtendedLimitInformation, infoPtr, (uint)infoSize);
                }
                finally { Marshal.FreeHGlobal(infoPtr); }

                NativeMethods.AssignProcessToJobObject(_jobHandle, _processHandle);
            }
            catch { }
        }

        private void StartReading()
        {
            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY-Read" };
            _readThread.Start();
        }

        private void StartProcessWait()
        {
            _processWaitThread = new Thread(() =>
            {
                try
                {
                    NativeMethods.WaitForSingleObject(_processHandle, NativeMethods.INFINITE);
                    if (!_isDisposed && _pseudoConsoleHandle != IntPtr.Zero)
                    {
                        NativeMethods.ClosePseudoConsole(_pseudoConsoleHandle);
                        _pseudoConsoleHandle = IntPtr.Zero;
                    }
                }
                catch { }
            })
            { IsBackground = true, Name = "ConPTY-Wait" };
            _processWaitThread.Start();
        }

        private void ReadLoop()
        {
            byte[] buffer = new byte[4096];
            try
            {
                while (!_isDisposed && _isRunning)
                {
                    int bytesRead = _outputReader.Read(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        byte[] data = new byte[bytesRead];
                        Array.Copy(buffer, data, bytesRead);
                        _outputQueue.Enqueue(data);
                    }
                    else break;
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                if (_isRunning)
                {
                    _isRunning = false;
                    try { ProcessExited?.Invoke(this, EventArgs.Empty); } catch { }
                }
            }
        }

        private void FlushOutputQueue(object state)
        {
            if (_isDisposed || !_isRunning) return;

            var allData = new List<byte>();
            if (_utf8Remainder.Length > 0)
            {
                allData.AddRange(_utf8Remainder);
                _utf8Remainder = new byte[0];
            }

            byte[] data;
            while (_outputQueue.TryDequeue(out data))
                allData.AddRange(data);

            if (allData.Count > 0)
            {
                byte[] remainder;
                byte[] complete = StripIncompleteUtf8Tail(allData.ToArray(), out remainder);
                _utf8Remainder = remainder;
                if (complete.Length > 0)
                {
                    try { DataReceived?.Invoke(complete); } catch { }
                }
            }
        }

        private static byte[] StripIncompleteUtf8Tail(byte[] data, out byte[] remainder)
        {
            int splitAt = data.Length;
            for (int i = data.Length - 1; i >= 0 && i >= data.Length - 4; i--)
            {
                byte b = data[i];
                if (b >= 0xF0) { if (data.Length - i < 4) splitAt = i; break; }
                else if (b >= 0xE0) { if (data.Length - i < 3) splitAt = i; break; }
                else if (b >= 0xC0) { if (data.Length - i < 2) splitAt = i; break; }
                else if (b < 0x80) break;
            }

            if (splitAt == data.Length) { remainder = new byte[0]; return data; }

            remainder = new byte[data.Length - splitAt];
            Array.Copy(data, splitAt, remainder, 0, remainder.Length);
            byte[] complete = new byte[splitAt];
            Array.Copy(data, 0, complete, 0, splitAt);
            return complete;
        }

        public void Stop()
        {
            if (_isDisposed || !_isRunning) return;
            Cleanup();
        }

        /// <summary>
        /// Tear down the terminal WITHOUT ever hanging the caller. KILL the child-process tree first
        /// (fast, kernel-level), then run the parts that can deadlock (ClosePseudoConsole, pipe Dispose,
        /// the INFINITE process-wait thread join) on a background worker bounded by a short Join. If the
        /// worker overruns, abandon it — the process is already dead, so the leaked handles are reclaimed
        /// when the host process exits. This is why Clarion no longer stalls on close.
        /// </summary>
        private void Cleanup()
        {
            _isRunning = false;
            lock (_liveLock) { _live.Remove(this); }
            try { _outputTimer?.Dispose(); } catch { }

            KillProcessTree();   // kill FIRST so nothing below can block waiting on live children

            var worker = new Thread(CleanupCore) { IsBackground = true, Name = "ConPTY-Cleanup" };
            worker.Start();
            worker.Join(1500);   // bounded — if the native closes deadlock, abandon (process already killed)
        }

        /// <summary>Fast, idempotent kill of the whole child-process tree (the job object kills pwsh + descendants).</summary>
        private void KillProcessTree()
        {
            try
            {
                if (_jobHandle != IntPtr.Zero)
                    NativeMethods.TerminateJobObject(_jobHandle, 0);
                if (_processHandle != IntPtr.Zero)
                {
                    uint exitCode;
                    if (NativeMethods.GetExitCodeProcess(_processHandle, out exitCode) && exitCode == NativeMethods.STILL_ACTIVE)
                        NativeMethods.TerminateProcess(_processHandle, 0);
                }
            }
            catch { }
        }

        /// <summary>The potentially-blocking native teardown — always run on a bounded background worker.</summary>
        private void CleanupCore()
        {
            try
            {
                if (_processWaitThread != null && _processWaitThread.IsAlive)
                    _processWaitThread.Join(800);

                if (_pseudoConsoleHandle != IntPtr.Zero)
                {
                    NativeMethods.ClosePseudoConsole(_pseudoConsoleHandle);
                    _pseudoConsoleHandle = IntPtr.Zero;
                }

                try { _inputWriter?.Dispose(); } catch { }
                try { _outputReader?.Dispose(); } catch { }
                try { _inputReadSide?.Dispose(); } catch { }
                try { _inputWriteSide?.Dispose(); } catch { }
                try { _outputReadSide?.Dispose(); } catch { }
                try { _outputWriteSide?.Dispose(); } catch { }

                if (_processHandle != IntPtr.Zero) { NativeMethods.CloseHandle(_processHandle); _processHandle = IntPtr.Zero; }
                if (_threadHandle != IntPtr.Zero) { NativeMethods.CloseHandle(_threadHandle); _threadHandle = IntPtr.Zero; }
                if (_jobHandle != IntPtr.Zero) { NativeMethods.CloseHandle(_jobHandle); _jobHandle = IntPtr.Zero; }
                if (_attributeList != IntPtr.Zero)
                {
                    NativeMethods.DeleteProcThreadAttributeList(_attributeList);
                    Marshal.FreeHGlobal(_attributeList);
                    _attributeList = IntPtr.Zero;
                }

                if (_readThread != null && _readThread.IsAlive)
                    _readThread.Join(800);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Cleanup();
        }

        /// <summary>
        /// Shutdown-only fast path: kill the child-process tree immediately and DON'T wait on any native
        /// close. Called by ShutdownService for every live terminal before the IDE tears down natively.
        /// Skipping ClosePseudoConsole / pipe disposes is deliberate — they can block, and the OS reclaims
        /// those handles when Clarion exits. Getting the processes dead is what lets the IDE close.
        /// </summary>
        public void ForceKillForShutdown()
        {
            _isRunning = false;
            _isDisposed = true;
            lock (_liveLock) { _live.Remove(this); }
            try { _outputTimer?.Dispose(); } catch { }
            KillProcessTree();
        }

        /// <summary>Kill every live terminal's child-process tree. Best-effort, never throws.</summary>
        public static void KillAllForShutdown()
        {
            List<ConPtyTerminal> snapshot;
            lock (_liveLock) { snapshot = new List<ConPtyTerminal>(_live); }
            foreach (var t in snapshot)
            {
                try { t.ForceKillForShutdown(); } catch { }
            }
        }
    }
}
