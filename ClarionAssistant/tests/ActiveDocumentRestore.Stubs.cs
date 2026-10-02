using System;
using System.Collections.Generic;
using System.IO;

// Stand-ins for the IDE surface ActiveDocumentRestoreCommand.cs touches, so the REAL class compiles
// standalone (PR #237). Only the members it uses: the workbench and its windows, FileService's open-file
// lookups, the solution path, the CA Editor live-tab probe and the log. The fake workbench itself (a real
// Form, so BeginInvoke and the WinForms timers behave as in the IDE) lives in the test.
namespace ICSharpCode.Core
{
    public interface ICommand
    {
        object Owner { get; set; }
        event EventHandler OwnerChanged;
        void Run();
    }
}

namespace ICSharpCode.SharpDevelop.Gui
{
    public interface IWorkbenchWindow
    {
        string ToolTipText { get; }
        void SelectWindow();
    }

    public interface IWorkbench
    {
        IWorkbenchWindow ActiveWorkbenchWindow { get; }
        event EventHandler ActiveWorkbenchWindowChanged;
    }

    public static class WorkbenchSingleton
    {
        public static IWorkbench Workbench { get; set; }
    }
}

namespace ICSharpCode.SharpDevelop
{
    using ICSharpCode.SharpDevelop.Gui;

    public static class FileService
    {
        public static readonly List<IWorkbenchWindow> Open = new List<IWorkbenchWindow>();

        public static IWorkbenchWindow GetOpenFile(string path)
        {
            foreach (var w in Open)
                if (string.Equals(Path.GetFullPath(w.ToolTipText), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return w;
            return null;
        }

        public static bool IsOpen(string path) { return GetOpenFile(path) != null; }
    }
}

namespace ClarionAssistant
{
    public static class MonacoSpikeLog
    {
        public static string DataDir;
        public static readonly List<string> Lines = new List<string>();
        public static void EnsureDir() { Directory.CreateDirectory(DataDir); }
        public static void Write(string message) { Lines.Add(message); }
    }

    /// <summary>.clw tabs are CA Editors (recorded through NotifyTabShown); anything else is not.</summary>
    public class MonacoClarionEditor
    {
        internal static bool TryGetLiveTabState(string path, out bool isDirty)
        {
            isDirty = false;
            return path != null && path.EndsWith(".clw", StringComparison.OrdinalIgnoreCase);
        }
    }
}

namespace ClarionAssistant.Services
{
    public static class EditorService
    {
        public static string OpenSolution;
        public static string GetOpenSolutionPath() { return OpenSolution; }
    }
}
