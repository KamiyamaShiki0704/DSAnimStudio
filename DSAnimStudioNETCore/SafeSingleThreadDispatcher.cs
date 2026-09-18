using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DSAnimStudio
{
    public class SafeSingleThreadDispatcher : IDisposable
    {
        private readonly object sync = new();
        private readonly AutoResetEvent wake = new(false);
        private readonly Queue<(Action Action, TaskCompletionSource Completion)> pending = new();
        private readonly Thread thread;
        private bool stopRequested;

        public SafeSingleThreadDispatcher()
        {
            thread = new Thread(ThreadProc) { IsBackground = true, Name = "Audio dispatcher" };
            thread.Start();
        }

        public void Invoke(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            // Nested engine calls already run on the required thread.
            if (Thread.CurrentThread == thread) { action(); return; }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (sync)
            {
                if (stopRequested) return;
                pending.Enqueue((action, completion));
                wake.Set();
            }
            // Each call owns its completion. If its caller is interrupted during
            // loading cancellation, finishing this work cannot release another caller.
            completion.Task.GetAwaiter().GetResult();
        }

        public void RequestStop()
        {
            lock (sync)
            {
                if (!stopRequested) { stopRequested = true; wake.Set(); }
            }
            if (Thread.CurrentThread != thread) thread.Join();
        }

        private void ThreadProc()
        {
            try
            {
                while (true)
                {
                    wake.WaitOne();
                    while (true)
                    {
                        (Action Action, TaskCompletionSource Completion) work;
                        lock (sync)
                        {
                            if (pending.Count == 0)
                            {
                                if (stopRequested) return;
                                break;
                            }
                            work = pending.Dequeue();
                        }
                        try { work.Action(); work.Completion.SetResult(); }
                        catch (Exception ex) { work.Completion.SetException(ex); }
                    }
                }
            }
            finally { wake.Dispose(); }
        }

        public void Dispose() => RequestStop();
    }
}
