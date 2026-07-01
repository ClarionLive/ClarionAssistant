using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ICSharpCode.Core;

namespace ClarionAssistant
{
    /// <summary>
    /// /Workspace/Autostart command (registered FIRST) — installs an AppDomain AssemblyResolve shim so
    /// our CWBinding/CommonSources-derived types (MonacoClarionEditorDisplayBinding / MonacoClarionEditor)
    /// load against WHATEVER Clarion 12 build is installed, not just the one we compiled against.
    ///
    /// ROOT CAUSE (ticket 0abd79df): CommonSources.dll is STRONG-NAMED
    /// (PublicKeyToken=c1f7e0138d9add58), so the CLR version-locks our reference. The build machine has
    /// C12 build 12.0.0.14000, so ClarionAssistant.dll records "CommonSources, Version=12.0.0.14000".
    /// On any C12 with a different build number (e.g. the public 12.0.0.13941), the strong-name bind
    /// FAILS and SharpDevelop reports "Cannot create object: ClarionAssistant.MonacoClarionEditorDisplayBinding"
    /// the moment a .clw is opened (the binding's CreateClarionEditor returns a CommonSources type).
    /// CWBinding.dll is NOT strong-named, so it binds by simple name and was never the problem.
    ///
    /// FIX: these editor internals are ABI-stable across minor C12 builds, so when the strong-name bind
    /// fails we redirect it to the copy Clarion's OWN backend already loaded (any build), or load it from
    /// the installed backend folder. Version stamp ignored; types unify.
    ///
    /// Runs at workbench load, before a source file's DisplayBinding is instantiated. The handler is
    /// process-lifetime, so every later binding creation benefits. Idempotent; guarded — MUST NOT throw.
    /// </summary>
    public class ClarionBindingAssemblyResolverCommand : ICommand
    {
        private object _owner;
        public object Owner
        {
            get { return _owner; }
            set { _owner = value; var h = OwnerChanged; if (h != null) h(this, EventArgs.Empty); }
        }
        public event EventHandler OwnerChanged;

        // Strong-name-version-locked SoftVelocity editor assemblies we must bind version-agnostically.
        private static readonly string[] Targets = { "CommonSources", "CWBinding" };
        private static bool _installed;

        public void Run()
        {
            try { Install(); }
            catch (Exception ex) { MonacoSpikeLog.Write("binding-resolver install error: " + ex.Message); }

            // Self-diagnostic: force-resolve the binding type NOW (via the shim) so the log self-confirms
            // the fix before the user opens a file — logs success or the exact exception + LoaderExceptions.
            try { Probe(); }
            catch (Exception ex) { MonacoSpikeLog.Write("binding-resolver probe error: " + ex.Message); }
        }

        internal static void Install()
        {
            if (_installed) return;
            _installed = true;
            AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
            MonacoSpikeLog.Write("binding-resolver installed (version-agnostic bind for CommonSources/CWBinding)");
        }

        private static Assembly OnResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string reqName = new AssemblyName(args.Name).Name;
                if (Array.IndexOf(Targets, reqName) < 0) return null;   // not ours — let the CLR continue

                // 1) Prefer the copy Clarion's own code backend already loaded (whatever build is installed).
                //    The loader does NOT re-check the version of an assembly returned from AssemblyResolve,
                //    so returning 13941 for a 14000 request satisfies the failed strong-name bind.
                Assembly loaded = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(SafeName(a), reqName, StringComparison.OrdinalIgnoreCase));
                if (loaded != null)
                {
                    MonacoSpikeLog.Write("binding-resolver: '" + args.Name + "' -> already-loaded " + loaded.GetName().Version);
                    return loaded;
                }

                // 2) Not loaded yet — load the installed build from the Clarion backend folder.
                string path = FindOnDisk(reqName);
                if (path != null && File.Exists(path))
                {
                    Assembly asm = Assembly.LoadFrom(path);
                    MonacoSpikeLog.Write("binding-resolver: '" + args.Name + "' -> LoadFrom " + path + " (" + asm.GetName().Version + ")");
                    return asm;
                }

                MonacoSpikeLog.Write("binding-resolver: '" + args.Name + "' UNRESOLVED (not loaded, not found on disk)");
                return null;
            }
            catch (Exception ex) { MonacoSpikeLog.Write("binding-resolver resolve error: " + ex.Message); return null; }
        }

        private static string SafeName(Assembly a)
        {
            try { return a.GetName().Name; } catch { return null; }
        }

        // <ClarionRoot>\bin\Addins\BackendBindings\ClarionBinding\{ClarionWin\CWBinding.dll | Common\CommonSources.dll}.
        // ClarionRoot is derived from our own deployed location: <ClarionRoot>\accessory\addins\ClarionAssistant\<us>.dll.
        private static string FindOnDisk(string simpleName)
        {
            try
            {
                string ourDir = Path.GetDirectoryName(typeof(ClarionBindingAssemblyResolverCommand).Assembly.Location);
                if (string.IsNullOrEmpty(ourDir)) return null;
                var d = new DirectoryInfo(ourDir);                 // ...\accessory\addins\ClarionAssistant
                DirectoryInfo root = d.Parent != null && d.Parent.Parent != null ? d.Parent.Parent.Parent : null; // -> <ClarionRoot>
                if (root == null) return null;
                string bind = Path.Combine(root.FullName, @"bin\Addins\BackendBindings\ClarionBinding");
                if (simpleName == "CWBinding")     return Path.Combine(bind, @"ClarionWin\CWBinding.dll");
                if (simpleName == "CommonSources") return Path.Combine(bind, @"Common\CommonSources.dll");
                return null;
            }
            catch { return null; }
        }

        // Force type resolution + instantiation exactly as SharpDevelop's CreateObject does, so the VM log
        // confirms the shim works (or names the precise failure) at startup instead of on first file open.
        private static void Probe()
        {
            try
            {
                Type t = typeof(ClarionBindingAssemblyResolverCommand).Assembly
                    .GetType("ClarionAssistant.MonacoClarionEditorDisplayBinding", throwOnError: true);
                object inst = Activator.CreateInstance(t);
                MonacoSpikeLog.Write("binding-resolver PROBE OK: instantiated " + inst.GetType().FullName);
            }
            catch (Exception ex)
            {
                MonacoSpikeLog.Write("binding-resolver PROBE FAIL: " + ex.GetType().FullName + ": " + ex.Message);
                var rtle = ex as ReflectionTypeLoadException;
                if (rtle != null && rtle.LoaderExceptions != null)
                    foreach (var le in rtle.LoaderExceptions)
                        MonacoSpikeLog.Write("   LoaderException: " + (le != null ? le.ToString() : "(null)"));
                for (Exception e = ex.InnerException; e != null; e = e.InnerException)
                    MonacoSpikeLog.Write("   Inner: " + e.GetType().FullName + ": " + e.Message);
            }
        }
    }
}
