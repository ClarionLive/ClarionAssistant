using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ClarionAssistant.Services
{
    /// <summary>
    /// GH #205 pipeline run 1: ask several IDEs the open-tab question AT ONCE, inside ONE overall budget, and remember an
    /// IDE that just ran out of time so it does not add its timeout to every following write.
    ///
    /// Each IDE is asked on its own pool task; <see cref="Ask{T}"/> waits for all of them up to the budget (plus a short
    /// grace for the per-call timeouts to report themselves). An IDE that did not answer in time, or answered TimedOut,
    /// is QUIET for <see cref="QuietMs"/>: during that window it is reported unreachable with when it will be asked
    /// again, without being asked. Its first real answer clears it. A refusal or a fast failure (connection refused) is
    /// reported as it is and never makes an IDE quiet: those cost nothing to repeat.
    ///
    /// IDE-free and transport-free (the asking is a delegate), so a harness can drive it with slow fakes and a clock.
    /// </summary>
    public sealed class IdeFanOut
    {
        public int QuietMs = 60000;
        public int GraceMs = 300;
        public Func<DateTime> Now = () => DateTime.UtcNow;

        private readonly object _lock = new object();
        private readonly Dictionary<int, DateTime> _quietUntil = new Dictionary<int, DateTime>();

        /// <summary>True while <paramref name="pid"/> is being skipped. For tests and logs.</summary>
        public bool IsQuiet(int pid)
        {
            lock (_lock)
            {
                DateTime until;
                return _quietUntil.TryGetValue(pid, out until) && until > Now();
            }
        }

        public List<TabProbe> Ask<T>(IList<T> endpoints, Func<T, int> pidOf, Func<T, TabProbe> ask, int budgetMs)
        {
            int n = endpoints == null ? 0 : endpoints.Count;
            var results = new TabProbe[n];
            var tasks = new Task[n];
            var started = new List<Task>();
            DateTime now = Now();

            for (int i = 0; i < n; i++)
            {
                int pid = pidOf(endpoints[i]);
                DateTime until;
                bool quiet;
                lock (_lock) quiet = _quietUntil.TryGetValue(pid, out until) && until > now;
                if (quiet)
                {
                    results[i] = new TabProbe
                    {
                        Ide = "IDE " + pid, Reachable = false,
                        Reason = "did not answer in time recently, so it was not asked (asked again after "
                                 + until.ToLocalTime().ToString("HH:mm:ss") + ")"
                    };
                    continue;
                }
                int k = i;
                var ep = endpoints[i];
                tasks[i] = Task.Run(() =>
                {
                    TabProbe r;
                    try { r = ask(ep); }
                    catch (Exception ex) { r = new TabProbe { Reachable = false, Reason = "the check failed: " + ex.Message }; }
                    lock (_lock) { if (results[k] == null) results[k] = r; }
                });
                started.Add(tasks[i]);
            }

            if (started.Count > 0)
            {
                try { Task.WaitAll(started.ToArray(), budgetMs + GraceMs); }
                catch (AggregateException) { }   // a faulted task already wrote its answer above
            }

            var answers = new List<TabProbe>(n);
            lock (_lock)
            {
                DateTime later = Now();
                for (int i = 0; i < n; i++)
                {
                    if (tasks[i] == null) { answers.Add(results[i]); continue; }   // quiet: not asked
                    int pid = pidOf(endpoints[i]);
                    var r = results[i];
                    if (r == null)
                    {
                        // Still running: the overall budget is spent. Claim the slot so a late answer is discarded.
                        r = results[i] = new TabProbe
                        {
                            Ide = "IDE " + pid, Reachable = false, TimedOut = true,
                            Reason = "did not answer within " + budgetMs + " ms"
                        };
                    }
                    if (r.TimedOut) _quietUntil[pid] = later.AddMilliseconds(QuietMs);
                    else if (r.Reachable) _quietUntil.Remove(pid);
                    answers.Add(r);
                }
            }
            return answers;
        }
    }
}
