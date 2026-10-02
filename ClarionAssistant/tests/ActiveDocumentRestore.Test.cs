using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ClarionAssistant;
using ClarionAssistant.Services;
using ICSharpCode.SharpDevelop;
using ICSharpCode.SharpDevelop.Gui;

// PR #237: ActiveDocumentRestoreCommand re-selects the tab that was active when a solution closed. The REAL
// class runs against a fake workbench (a real Form, so its BeginInvoke posts and WinForms timers behave as
// in the IDE) and drives the IDE's sequence by hand: SolutionLoaded, views opening, the synchronous reopen
// loop, tab clicks, close. Guards the review finding that a view opening BEFORE the reopen loop (the .app
// tab when the solution comes from Recent Applications) made the restore give up and let the loop record
// its last file over the saved choice - for good.
//
// Takes ~30 s: the class's own timings (2.5 s quiet window, 2 s verify) run in real time.
// Run:  tests\Run-Tests.ps1 -CSharpOnly
static class ActiveDocumentRestoreTest
{
    static int pass = 0, fail = 0;
    static void Ok(string name, bool cond, string detail)
    {
        if (cond) { pass++; Console.WriteLine("  [ok]   " + name); }
        else { fail++; Console.WriteLine("  [FAIL] " + name + (detail != null ? "  -> " + detail : "")); }
    }

    sealed class FakeWindow : IWorkbenchWindow
    {
        public readonly string Path;
        public FakeWindow(string path) { Path = path; }
        public string ToolTipText { get { return Path; } }
        public void SelectWindow() { Wb.Activate(this); }
    }

    sealed class FakeWorkbench : Form, IWorkbench
    {
        public IWorkbenchWindow ActiveWorkbenchWindow { get; private set; }
        public event EventHandler ActiveWorkbenchWindowChanged;
        public event EventHandler ViewOpened;

        public void Activate(FakeWindow w)
        {
            ActiveWorkbenchWindow = w;
            var h = ActiveWorkbenchWindowChanged; if (h != null) h(this, EventArgs.Empty);
            // A CA Editor tab reports itself when its overlay becomes visible (MonacoClarionEditor.AttachOverlay).
            bool dirty;
            if (MonacoClarionEditor.TryGetLiveTabState(w.Path, out dirty)) ActiveDocumentRestoreCommand.NotifyTabShown(w.Path);
        }

        public void RaiseViewOpened() { var h = ViewOpened; if (h != null) h(this, EventArgs.Empty); }
    }

    static FakeWorkbench Wb;
    static string Dir, A, B, C, App;

    static string Store { get { return Path.Combine(MonacoSpikeLog.DataDir, "active-documents.txt"); } }

    static void Raise(string handler)
    {
        typeof(ActiveDocumentRestoreCommand).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { null, EventArgs.Empty });
    }

    /// <summary>Opens a tab the way the IDE does: shown + activated, then ViewOpened (or the other order).</summary>
    static void OpenView(string path, bool viewOpenedFirst)
    {
        var w = new FakeWindow(path);
        FileService.Open.Add(w);
        if (viewOpenedFirst) { Wb.RaiseViewOpened(); Wb.Activate(w); }
        else { Wb.Activate(w); Wb.RaiseViewOpened(); }
    }

    /// <summary>The IDE's reopen: ONE synchronous UI-thread loop, no messages pumped in between.</summary>
    static void ReopenLoop(bool viewOpenedFirst, params string[] paths)
    {
        foreach (var p in paths) OpenView(p, viewOpenedFirst);
    }

    static void Click(string path) { ((FakeWindow)FileService.GetOpenFile(path)).SelectWindow(); }

    static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(10); }
    }

    static string Saved(string solution)
    {
        if (!File.Exists(Store)) return null;
        foreach (var line in File.ReadAllLines(Store))
            if (line.StartsWith(solution + "\t", StringComparison.OrdinalIgnoreCase)) return line.Substring(solution.Length + 1);
        return null;
    }

    static void Seed(string solution, string active)
    {
        var lines = new List<string>();
        if (File.Exists(Store))
            foreach (var l in File.ReadAllLines(Store)) if (!l.StartsWith(solution + "\t", StringComparison.OrdinalIgnoreCase)) lines.Add(l);
        lines.Add(solution + "\t" + active);
        File.WriteAllLines(Store, lines.ToArray());
    }

    static string Active { get { var w = Wb.ActiveWorkbenchWindow; return w == null ? null : Path.GetFileName(w.ToolTipText); } }
    static string Name(string p) { return p == null ? "(none)" : Path.GetFileName(p); }

    /// <summary>Opens a solution whose saved active file is b.clw.</summary>
    static string Load(string name)
    {
        string sol = Path.Combine(Dir, name + ".sln");
        File.WriteAllText(sol, "");
        Seed(sol, B);
        EditorService.OpenSolution = sol;
        Raise("OnSolutionLoaded");
        return sol;
    }

    static void Close()
    {
        Raise("OnPreferencesSaving");
        FileService.Open.Clear();
        EditorService.OpenSolution = null;
        Raise("OnSolutionClosing");
        Raise("OnSolutionClosed");
        Pump(100);
    }

    static void Dump()
    {
        foreach (var l in MonacoSpikeLog.Lines) Console.WriteLine("         log: " + l);
    }

    [STAThread]
    static int Main()
    {
        Dir = Path.Combine(Path.GetTempPath(), "ca-activedoc-" + Process.GetCurrentProcess().Id);
        Directory.CreateDirectory(Dir);
        MonacoSpikeLog.DataDir = Path.Combine(Dir, "data");
        A = Path.Combine(Dir, "a.clw"); B = Path.Combine(Dir, "b.clw"); C = Path.Combine(Dir, "c.clw"); App = Path.Combine(Dir, "x.app");
        foreach (var f in new[] { A, B, C, App }) File.WriteAllText(f, "");
        MonacoSpikeLog.EnsureDir();

        Wb = new FakeWorkbench();
        var handle = Wb.Handle;          // BeginInvoke needs a created handle, as on the IDE's main window
        WorkbenchSingleton.Workbench = Wb;
        new ActiveDocumentRestoreCommand().Run();

        try
        {
            // 1. Plain reopen: only the reopen loop opens views. The saved tab wins, and the next click records.
            Console.WriteLine("1. plain reopen");
            MonacoSpikeLog.Lines.Clear();
            string sol = Load("plain");
            ReopenLoop(false, A, B, C);
            Pump(3000);
            Ok("saved tab b.clw selected after the reopen loop", Active == "b.clw", "active " + Active);
            Ok("saved choice kept", Name(Saved(sol)) == "b.clw", "saved " + Name(Saved(sol)));
            Click(C);
            Ok("a tab click after the restore is recorded", Name(Saved(sol)) == "c.clw", "saved " + Name(Saved(sol)));
            if (fail > 0) Dump();
            Close();

            // 2. Recent Applications: the .app tab opens first, the reopen loop shortly after (both event orders).
            foreach (bool order in new[] { false, true })
            {
                Console.WriteLine("2. .app view opens before the reopen loop (ViewOpened " + (order ? "before" : "after") + " activation)");
                MonacoSpikeLog.Lines.Clear();
                int f0 = fail;
                sol = Load("recent" + (order ? "1" : "0"));
                OpenView(App, order);
                Pump(600);                       // the .app view's post is delivered before the loop arrives
                ReopenLoop(order, A, B, C);
                Pump(3000);
                Ok("saved tab b.clw selected after the reopen loop", Active == "b.clw", "active " + Active);
                Ok("the reopen loop did not overwrite the saved choice", Name(Saved(sol)) == "b.clw", "saved " + Name(Saved(sol)));
                if (fail > f0) Dump();
                Close();
            }

            // 3. Same, but the reopen loop arrives after the quiet window has given up: the record must survive
            //    the burst, and the target is still selected once it opens.
            Console.WriteLine("3. reopen loop arrives after the quiet-window give-up");
            MonacoSpikeLog.Lines.Clear();
            int f1 = fail;
            sol = Load("late");
            OpenView(App, false);
            Pump(3500);
            ReopenLoop(false, A, B, C);
            Pump(3000);
            Ok("the late reopen loop did not overwrite the saved choice", Name(Saved(sol)) == "b.clw", "saved " + Name(Saved(sol)));
            Ok("saved tab b.clw still selected once it opened", Active == "b.clw", "active " + Active);
            if (fail > f1) Dump();
            Close();

            // 3b. A late reopen loop WITHOUT the saved file: only the hold keeps its tabs from being recorded.
            Console.WriteLine("3b. late reopen loop without the saved file");
            MonacoSpikeLog.Lines.Clear();
            int f3 = fail;
            sol = Load("latemissing");
            OpenView(App, false);
            Pump(3500);
            ReopenLoop(false, A, C);
            Pump(500);
            Ok("the late burst did not overwrite the saved choice", Name(Saved(sol)) == "b.clw", "saved " + Name(Saved(sol)));
            if (fail > f3) Dump();
            Close();

            // 4. The saved file is not in the reopened set: nothing to select, the burst records nothing, and
            //    the developer's next tab click is recorded again.
            Console.WriteLine("4. saved file no longer reopened");
            MonacoSpikeLog.Lines.Clear();
            int f2 = fail;
            sol = Load("gone");
            ReopenLoop(false, A, C);
            Pump(3500);
            Ok("the burst did not overwrite the saved choice", Name(Saved(sol)) == "b.clw", "saved " + Name(Saved(sol)));
            Click(A);
            Pump(300);
            Ok("the developer's next tab click is recorded", Name(Saved(sol)) == "a.clw", "saved " + Name(Saved(sol)));
            if (fail > f2) Dump();
            Close();
        }
        catch (Exception ex) { fail++; Console.WriteLine("  [FAIL] threw: " + ex); Dump(); }

        // 5. The store write goes through a temp file named per process (two IDE instances share the store),
        //    and a swap that fails still removes it. A directory squatting on the store's name fails the swap.
        Console.WriteLine("5. failed store swap");
        string[] leftovers = Directory.GetFiles(MonacoSpikeLog.DataDir, "*.tmp");
        Ok("no temp file left beside the store after the normal writes", leftovers.Length == 0, string.Join(", ", leftovers));
        try
        {
            File.Delete(Store);
            Directory.CreateDirectory(Store);
            MonacoSpikeLog.Lines.Clear();
            EditorService.OpenSolution = Path.Combine(Dir, "swap.sln");
            Raise("OnSolutionLoaded");       // nothing saved: plain recording
            OpenView(A, false);              // records a.clw -> the swap fails
            Ok("the failed swap was logged", MonacoSpikeLog.Lines.Exists(l => l.Contains("write failed")), string.Join(" | ", MonacoSpikeLog.Lines));
            leftovers = Directory.GetFiles(MonacoSpikeLog.DataDir, "*.tmp");
            Ok("a failed swap leaves no temp file behind", leftovers.Length == 0, string.Join(", ", leftovers));
            Close();
        }
        catch (Exception ex) { fail++; Console.WriteLine("  [FAIL] threw: " + ex); }

        try { Directory.Delete(Dir, true); } catch { }
        Console.WriteLine(fail == 0 ? "PASS " + pass : "FAIL " + fail + " of " + (pass + fail));
        return fail == 0 ? 0 : 1;
    }
}
