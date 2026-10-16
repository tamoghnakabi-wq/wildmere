using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Wildmere.Core
{
    /// <summary>
    /// Fixed set of background threads pulling prioritised jobs (lower priority value runs first).
    /// Jobs run off the main thread and must not touch Unity objects; they hand results back with
    /// <see cref="Post"/>, which the main thread drains under a time budget.
    /// </summary>
    public sealed class WorkerPool : IDisposable
    {
        public sealed class Job
        {
            public float Priority;
            public Action<int> Run;     // receives the worker index (for per-thread scratch)
            public string Tag;
        }

        readonly List<Job> mJobs = new List<Job>();
        readonly object mLock = new object();
        readonly Thread[] mThreads;
        readonly ConcurrentQueue<Action> mMain = new ConcurrentQueue<Action>();
        volatile bool mStop;
        int mBusy;

        public int ThreadCount => mThreads.Length;
        public int Pending { get { lock (mLock) return mJobs.Count; } }
        public int Busy => Volatile.Read(ref mBusy);
        public int MainPending => mMain.Count;

        public WorkerPool(int threads)
        {
            mThreads = new Thread[threads];
            for (int i = 0; i < threads; i++)
            {
                int idx = i;
                mThreads[i] = new Thread(() => Loop(idx)) { IsBackground = true, Name = $"wm-worker-{i}", Priority = System.Threading.ThreadPriority.BelowNormal };
                mThreads[i].Start();
            }
        }

        public void Enqueue(Job job)
        {
            lock (mLock)
            {
                mJobs.Add(job);
                Monitor.Pulse(mLock);
            }
        }

        /// <summary>Re-score queued jobs (e.g. when the player moves) — f returns the new priority, or NaN to drop it.</summary>
        public void Reprioritise(Func<Job, float> f)
        {
            lock (mLock)
            {
                for (int i = mJobs.Count - 1; i >= 0; i--)
                {
                    float p = f(mJobs[i]);
                    if (float.IsNaN(p)) mJobs.RemoveAt(i);
                    else mJobs[i].Priority = p;
                }
            }
        }

        public void Post(Action a) => mMain.Enqueue(a);

        /// <summary>Run posted main-thread completions for at most <paramref name="budgetMs"/>.</summary>
        public void Drain(float budgetMs)
        {
            var start = Time.realtimeSinceStartupAsDouble;
            while (mMain.TryDequeue(out var a))
            {
                try { a(); }
                catch (Exception e) { Debug.LogException(e); }
                if ((Time.realtimeSinceStartupAsDouble - start) * 1000.0 > budgetMs) break;
            }
        }

        void Loop(int idx)
        {
            while (!mStop)
            {
                Job job = null;
                lock (mLock)
                {
                    while (!mStop && mJobs.Count == 0) Monitor.Wait(mLock, 250);
                    if (mStop) return;
                    int best = 0;
                    for (int i = 1; i < mJobs.Count; i++)
                        if (mJobs[i].Priority < mJobs[best].Priority) best = i;
                    job = mJobs[best];
                    mJobs[best] = mJobs[mJobs.Count - 1];
                    mJobs.RemoveAt(mJobs.Count - 1);
                }
                Interlocked.Increment(ref mBusy);
                try { job.Run(idx); }
                catch (Exception e) { mMain.Enqueue(() => Debug.LogError($"WM_WORKER_ERR {job.Tag}: {e}")); }
                finally { Interlocked.Decrement(ref mBusy); }
            }
        }

        public void Dispose()
        {
            mStop = true;
            lock (mLock) Monitor.PulseAll(mLock);
            foreach (var t in mThreads) t.Join(500);
        }
    }
}
