using System;
using System.Collections.Generic;
using System.Reflection;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// Subscribes one handler to named events of a source object, AT MOST ONCE per event name, however often it
    /// is called (GH #263 run-2 review). Each name is marked attempted BEFORE its subscription runs, so an event
    /// whose add throws, or one the source does not have, is never tried again; that way a retry cannot add a
    /// second copy of the handler to an event that did subscribe. Zero IDE coupling: GeneratedSourceWatch uses it
    /// on the workbench, and tests\EventWiringOnce.Test.cs uses it on a fake.
    /// </summary>
    public sealed class EventWiringOnce
    {
        private readonly HashSet<string> _attempted = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        /// <summary>Wire <paramref name="handler"/> (a static method taking (object, EventArgs)) to each event of
        /// <paramref name="source"/> in <paramref name="eventNames"/> not attempted before. Returns how many it
        /// subscribed on this call. A null source attempts nothing, so the caller can retry once it exists.</summary>
        public int WireAll(object source, IEnumerable<string> eventNames, MethodInfo handler, Action<string> log)
        {
            if (source == null || eventNames == null || handler == null) return 0;
            int wired = 0;
            foreach (var name in eventNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                lock (_gate) { if (!_attempted.Add(name)) continue; }
                try
                {
                    var evt = source.GetType().GetEvent(name, BindingFlags.Public | BindingFlags.Instance);
                    if (evt == null) { if (log != null) log(name + " not found"); continue; }
                    evt.AddEventHandler(source, Delegate.CreateDelegate(evt.EventHandlerType, handler));
                    wired++;
                    if (log != null) log("WIRED: " + name);
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    if (log != null) log(name + " wiring failed (not retried): " + inner.Message);
                }
            }
            return wired;
        }

        /// <summary>True once every name has been attempted (subscribed, missing, or failed).</summary>
        public bool AllAttempted(IEnumerable<string> eventNames)
        {
            lock (_gate)
            {
                foreach (var n in eventNames) if (!_attempted.Contains(n)) return false;
                return true;
            }
        }
    }
}
