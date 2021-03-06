/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿//#define TRACK_DETAILED_STATS
//#define SHOW_CPU_LOCKS
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Orleans.Runtime.Scheduler
{
    internal class WorkerPoolThread : AsynchAgent
    {
        private const int MAX_THREAD_COUNT_TO_REPLACE = 500;
        private const int MAX_CPU_USAGE_TO_REPLACE = 50;

        private readonly AutoResetEvent e;
        private readonly WorkerPool pool;
        private readonly OrleansTaskScheduler scheduler;
        private readonly TimeSpan maxWorkQueueWait;
        internal CancellationToken CancelToken { get { return Cts.Token; } }
        private bool ownsSemaphore;
        internal bool IsSystem { get; private set; }

        [ThreadStatic]
        private static WorkerPoolThread current;
        internal static WorkerPoolThread CurrentWorkerThread { get { return current; } }

        internal static RuntimeContext CurrentContext { get { return RuntimeContext.Current; } }

        // For status reporting
        private IWorkItem currentWorkItem;
        private Task currentTask;
        internal IWorkItem CurrentWorkItem
        {
            get { return currentWorkItem; }
            set
            {
                currentWorkItem = value;
                currentTask = null;
                CurrentStateStarted = DateTime.UtcNow;
            }
        }
        internal Task CurrentTask
        {
            get { return currentTask; }
            set
            {
                currentTask = value;
                currentWorkItem = null;
                CurrentStateStarted = DateTime.UtcNow;
            }
        }
        
        internal DateTime CurrentStateStarted { get; private set; }

        internal string GetThreadStatus()
        {
            // Take status snapshot before checking status, to avoid race
            Task task = currentTask;
            IWorkItem workItem = currentWorkItem;
            TimeSpan since = Utils.Since(CurrentStateStarted);

            if (task != null)
                return string.Format("Executing Task Id={0} Status={1} for {2}", task.Id, task.Status, since);

            return workItem != null ? 
                string.Format("Executing Work Item {0} for {1}", workItem, since) : 
                string.Format("Idle for {0}", since);
        }

        internal readonly int WorkerThreadStatisticsNumber;

        internal WorkerPoolThread(WorkerPool gtp, OrleansTaskScheduler sched, bool system = false)
            : base(system ? "System" : null)
        {
            e = new AutoResetEvent(false);
            pool = gtp;
            scheduler = sched;
            ownsSemaphore = false;
            IsSystem = system;
            maxWorkQueueWait = IsSystem ? Constants.INFINITE_TIMESPAN : gtp.MaxWorkQueueWait;
            OnFault = FaultBehavior.IgnoreFault;
            CurrentStateStarted = DateTime.UtcNow;
            CurrentWorkItem = null;
            if (StatisticsCollector.CollectTurnsStats)
                WorkerThreadStatisticsNumber = SchedulerStatisticsGroup.RegisterWorkingThread(Name);
        }

        protected override void Run()
        {
            try
            {
                // We can't set these in the constructor because that doesn't run on our thread
                current = this;
                RuntimeContext.InitializeThread(scheduler);
                
                int noWorkCount = 0;
                
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStartExecution();
                }
#endif

                // Until we're cancelled...
                while (!Cts.IsCancellationRequested)
                {
                    // Wait for a CPU
                    if (!IsSystem)
                        TakeCpu();
                    
                    try
                    {
#if DEBUG
                        if (Log.IsVerbose3) Log.Verbose3("Worker thread {0} - Waiting for {1} work item", this.ManagedThreadId, IsSystem ? "System" : "Any");
#endif
                        // Get some work to do
                        IWorkItem todo;

                        todo = IsSystem ? scheduler.RunQueue.GetSystem(Cts.Token, maxWorkQueueWait) : 
                            scheduler.RunQueue.Get(Cts.Token, maxWorkQueueWait);

#if TRACK_DETAILED_STATS
                        if (StatisticsCollector.CollectThreadTimeTrackingStats)
                        {
                            threadTracking.OnStartProcessing();
                        }
#endif
                        if (todo != null)
                        {
                            if (!IsSystem)
                                pool.RecordRunningThread();
                            

                            // Capture the queue wait time for this task
                            TimeSpan waitTime = todo.TimeSinceQueued;
                            if (waitTime > scheduler.DelayWarningThreshold && !Debugger.IsAttached)
                            {
                                SchedulerStatisticsGroup.NumLongQueueWaitTimes.Increment();
                                Log.Warn(ErrorCode.SchedulerWorkerPoolThreadQueueWaitTime, "Queue wait time of {0} for Item {1}", waitTime, todo);
                            }
#if DEBUG
                            if (Log.IsVerbose3) Log.Verbose3("Queue wait time for {0} work item is {1}", todo.ItemType, waitTime);
#endif
                            // Do the work
                            try
                            {
                                RuntimeContext.SetExecutionContext(todo.SchedulingContext, scheduler);
                                if (todo.ItemType != WorkItemType.WorkItemGroup)
                                {
                                    // for WorkItemGroup we will track CurrentWorkItem inside WorkItemGroup. 
                                    CurrentWorkItem = todo;
#if TRACK_DETAILED_STATS
                                    if (StatisticsCollector.CollectTurnsStats)
                                    {
                                        SchedulerStatisticsGroup.OnThreadStartsTurnExecution(WorkerThreadStatisticsNumber, todo.SchedulingContext);
                                    }
#endif
                                }
                                todo.Execute();
                            }
                            catch (ThreadAbortException ex)
                            {
                                // The current turn was aborted (indicated by the exception state being set to true).
                                // In this case, we just reset the abort so that life continues. No need to do anything else.
                                if ((ex.ExceptionState != null) && ex.ExceptionState.Equals(true))
                                    Thread.ResetAbort();
                                else
                                    Log.Error(ErrorCode.Runtime_Error_100029, "Caught thread abort exception, allowing it to propagate outwards", ex);
                            }
                            catch (Exception ex)
                            {
                                var errorStr = String.Format("Worker thread caught an exception thrown from task {0}.", todo);
                                Log.Error(ErrorCode.Runtime_Error_100030, errorStr, ex);
                            }
                            finally
                            {
#if TRACK_DETAILED_STATS
                                if (todo.ItemType != WorkItemType.WorkItemGroup)
                                {
                                    if (StatisticsCollector.CollectTurnsStats)
                                    {
                                        //SchedulerStatisticsGroup.OnTurnExecutionEnd(CurrentStateTime.Elapsed);
                                        SchedulerStatisticsGroup.OnTurnExecutionEnd(Utils.Since(CurrentStateStarted));
                                    }
                                    if (StatisticsCollector.CollectThreadTimeTrackingStats)
                                    {
                                        threadTracking.IncrementNumberOfProcessed();
                                    }
                                    CurrentWorkItem = null;
                                }
                                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                                {
                                    threadTracking.OnStopProcessing();
                                }
#endif
                                if (!IsSystem)
                                    pool.RecordIdlingThread();
                                
                                RuntimeContext.ResetExecutionContext();
                                noWorkCount = 0;
                            }
                        }
                        else // todo was null -- no work to do
                        {
                            if (Cts.IsCancellationRequested)
                            {
                                // Cancelled -- we're done
                                // Note that the finally block will release the CPU, since it will get invoked
                                // even for a break or a return
                                break;
                            }
                            noWorkCount++;
                        }
                    }
                    catch (ThreadAbortException tae)
                    {
                        // Can be reported from RunQueue.Get when Silo is being shutdown, so downgrade to verbose log
                        if (Log.IsVerbose) Log.Verbose("Received thread abort exception -- exiting. {0}", tae);
                        Thread.ResetAbort();
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ErrorCode.Runtime_Error_100031, "Exception bubbled up to worker thread", ex);
                        break;
                    }
                    finally
                    {
                        CurrentWorkItem = null; // Also sets CurrentTask to null

                        // Release the CPU
                        if (!IsSystem)
                            PutCpu();
                    }

                    // If we've gone a minute without any work to do, let's give up
                    if (!IsSystem && (maxWorkQueueWait.Multiply(noWorkCount) > TimeSpan.FromMinutes(1)) && pool.CanExit())
                    {
#if DEBUG
                        if (Log.IsVerbose) Log.Verbose("Scheduler thread leaving because there's not enough work to do");
#endif
                        break;
                    }
                }
            }
            catch (Exception exc)
            {
                Log.Error(ErrorCode.SchedulerWorkerThreadExc, "WorkerPoolThread caugth exception:", exc);
            }
            finally
            {
                if (!IsSystem)
                    pool.RecordLeavingThread(this);
                
#if TRACK_DETAILED_STATS
                if (StatisticsCollector.CollectThreadTimeTrackingStats)
                {
                    threadTracking.OnStopExecution();
                }
#endif
                CurrentWorkItem = null;
            }
        }

        internal void TakeCpu()
        {
            if (ownsSemaphore) return;

#if DEBUG && SHOW_CPU_LOCKS
            if (log.IsVerbose3) log.Verbose3("Worker thread {0} - TakeCPU", this.ManagedThreadId);
#endif
            pool.TakeCpu();
            ownsSemaphore = true;
        }

        internal void PutCpu()
        {
            if (!ownsSemaphore) return;

#if DEBUG && SHOW_CPU_LOCKS
            if (log.IsVerbose3) log.Verbose3("Worker thread {0} - PutCPU", this.ManagedThreadId);
#endif
            pool.PutCpu();
            ownsSemaphore = false;
        }

        public void DumpStatus(StringBuilder sb)
        {
            sb.AppendLine(ToString());
        }

        public override string ToString()
        {
            return String.Format("<{0}, ManagedThreadId={1}, {2}>",
                Name,
                ManagedThreadId,
                GetThreadStatus());
        }

        internal void CheckForLongTurns()
        {
            if ((CurrentWorkItem == null && CurrentTask == null) ||
                (Utils.Since(CurrentStateStarted) <= OrleansTaskScheduler.TurnWarningLengthThreshold)) return;

            // Since this thread is running a long turn, which (we hope) is blocked on some IO 
            // or other external process, we'll create a replacement thread and tell this thread to 
            // exit when it's done with the turn.
            // Note that we only do this if the current load is reasonably low and the current thread
            // count is reasonably small.
            if (!pool.InjectMoreWorkerThreads || pool.BusyWorkerCount >= MAX_THREAD_COUNT_TO_REPLACE ||
                (Silo.CurrentSilo == null || !(Silo.CurrentSilo.Metrics.CpuUsage < MAX_CPU_USAGE_TO_REPLACE))) return;

            if (Cts.IsCancellationRequested) return;

            // only create a new thread once per slow thread!
            Log.Warn(ErrorCode.SchedulerTurnTooLong2, string.Format(
                "Worker pool thread {0} (ManagedThreadId={1}) has been busy for long time: {2}; creating a new worker thread",
                Name, ManagedThreadId, GetThreadStatus()));
            Cts.Cancel();
            pool.CreateNewThread();
            // Consider: mark the activation running a long turn to reduce it's time quantum
        }

        internal bool DoHealthCheck()
        {
            if ((CurrentWorkItem == null && CurrentTask == null) ||
                (Utils.Since(CurrentStateStarted) <= OrleansTaskScheduler.TurnWarningLengthThreshold)) return true;

            Log.Error(ErrorCode.SchedulerTurnTooLong, string.Format(
                "Worker pool thread {0} (ManagedThreadId={1}) has been busy for long time: {2}",
                Name, ManagedThreadId, GetThreadStatus()));
            return false;
        }
    }
}
