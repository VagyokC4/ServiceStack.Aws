﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using ServiceStack.Logging;
using ServiceStack.Messaging;

namespace ServiceStack.Aws.Sqs
{
    // The majority of the code here was lifted/massaged from the existing MQ Server implmentations
    public abstract class BaseMqServer<TWorker> : IMessageService
        where TWorker : class, IMqWorker<TWorker>
    {
        protected readonly ILog log;
        protected readonly string typeName;
        private readonly object msgLock = new object();
        private readonly object statusLock = new object();

        protected int status;
        protected List<TWorker> workers;
        private Thread bgThread;
        private long bgThreadCount;

        private long timesStarted;
        private long doOperation = WorkerOperation.NoOp;
        private long noOfErrors = 0;
        private int noOfContinuousErrors = 0;
        private string lastExMsg = null;

        public BaseMqServer()
        {
            var type = GetType();

            log = LogManager.GetLogger(type);

            typeName = type.Name;

            this.ErrorHandler = ex => log.Error(string.Concat("Exception in ", typeName, " MQ Server: ", ex.Message), ex);
        }

        protected abstract void Init();

        public abstract void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn);

        public abstract void RegisterHandler<T>(Func<IMessage<T>, object> processMessageFn,
                                                Action<IMessageHandler, IMessage<T>, Exception> processExceptionEx);

        /// <summary>
        /// The Message Factory used by this MQ Server
        /// </summary>
        public abstract IMessageFactory MessageFactory { get; }

        public IMessageHandlerStats GetStats()
        {
            lock (workers)
            {
                var total = new MessageHandlerStats("All Handlers");
                workers.ForEach(x => total.Add(x.GetStats()));
                return total;
            }
        }

        public string GetStatus()
        {
            lock (statusLock)
            {
                return WorkerStatus.ToString(status);
            }
        }

        public string GetStatsDescription()
        {
            lock (workers)
            {
                var sb = new StringBuilder("#MQ SERVER STATS:\n");
                sb.AppendLine("===============");
                sb.AppendLine("Current Status: " + GetStatus());
                sb.AppendLine("Listening On: " + string.Join(", ", workers.Select(x => x.QueueName).ToArray()));
                sb.AppendLine("Times Started: " + Interlocked.CompareExchange(ref timesStarted, 0, 0));
                sb.AppendLine("Num of Errors: " + Interlocked.CompareExchange(ref noOfErrors, 0, 0));
                sb.AppendLine("Num of Continuous Errors: " + Interlocked.CompareExchange(ref noOfContinuousErrors, 0, 0));
                sb.AppendLine("Last ErrorMsg: " + lastExMsg);
                sb.AppendLine("===============");

                foreach (var worker in workers)
                {
                    sb.AppendLine(worker.GetStats().ToString());
                    sb.AppendLine("---------------\n");
                }

                return sb.ToString();
            }
        }

        protected void WorkerErrorHandler(TWorker source, Exception ex)
        {
            log.Error(string.Concat("Received exception in Worker: ", source.QueueName), ex);

            var sourceWorker = workers.SingleOrDefault(w => ReferenceEquals(w, source));

            if (sourceWorker == null)
                return;

            log.Debug(string.Concat("Starting new ", source.QueueName, " worker..."));

            workers.Remove(sourceWorker);

            var newWorker = sourceWorker.Clone();
            workers.Add(newWorker);
            newWorker.Start();

            sourceWorker.Dispose();
            sourceWorker = null;
        }

        /// 
        /// <summary>
        /// Wait before Starting the MQ Server after a restart 
        /// </summary>
        public int? KeepAliveRetryAfterMs { get; set; }

        /// <summary>
        /// Wait (in seconds) before starting the MQ Server after a restart 
        /// </summary>
        public int? WaitBeforeNextRestart { get; set; }

        public List<Type> RegisteredTypes { get; private set; }

        public long BgThreadCount
        {
            get { return Interlocked.CompareExchange(ref bgThreadCount, 0, 0); }
        }

        /// <summary>
        /// Execute global error handler logic. Must be thread-safe.
        /// </summary>
        public Action<Exception> ErrorHandler { get; set; }

        /// <summary>
        /// If you only want to enable priority queue handlers (and threads) for specific msg types
        /// </summary>
        public string[] PriortyQueuesWhitelist { get; set; }

        /// <summary>
        /// Don't listen on any Priority Queues
        /// </summary>
        public bool DisablePriorityQueues
        {
            set
            {
                PriortyQueuesWhitelist = new string[0];
            }
        }

        /// <summary>
        /// Opt-in to only publish responses on this white list. 
        /// Publishes all responses by default.
        /// </summary>
        public string[] PublishResponsesWhitelist { get; set; }

        /// <summary>
        /// Don't publish any response messages
        /// </summary>
        public bool DisablePublishingResponses
        {
            set { PublishResponsesWhitelist = value ? new string[0] : null; }
        }

        protected abstract void DoDispose();

        void DisposeWorkerThreads()
        {
            log.Debug(string.Concat("Disposing all ", typeName, " MQ Server worker threads..."));

            if (workers != null)
            {
                workers.ForEach(w => w.Dispose());
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref status, 0, 0) == WorkerStatus.Disposed)
            {
                return;
            }

            Stop();

            if (Interlocked.CompareExchange(ref status, WorkerStatus.Disposed, WorkerStatus.Stopped) != WorkerStatus.Stopped)
            {
                Interlocked.CompareExchange(ref status, WorkerStatus.Disposed, WorkerStatus.Stopping);
            }

            try
            {
                DisposeWorkerThreads();
            }
            catch (Exception ex)
            {
                log.Error("Error DisposeWorkerThreads(): ", ex);
            }

            try
            {   // Give a small time slice to die gracefully
                Thread.Sleep(100);
                KillBgThreadIfExists();
            }
            catch (Exception ex)
            {
                if (this.ErrorHandler != null)
                {
                    this.ErrorHandler(ex);
                }
            }

            DoDispose();
        }

        public void Start()
        {
            if (Interlocked.CompareExchange(ref status, 0, 0) == WorkerStatus.Started)
            {   // Already started, (re)start workers as needed and done
                StartWorkerThreads();
                return;
            }

            if (Interlocked.CompareExchange(ref status, 0, 0) == WorkerStatus.Disposed)
            {
                throw new ObjectDisposedException(string.Concat(typeName, " MQ Host has been disposed"));
            }

            if (Interlocked.CompareExchange(ref status, WorkerStatus.Starting, WorkerStatus.Stopped) != WorkerStatus.Stopped)
            {
                return;
            }

            // 1-thread now from here on
            try
            {
                Init();

                if (workers == null || workers.Count == 0)
                {
                    log.Warn(string.Concat("Cannot start ", typeName, " MQ Server with no Message Handlers registered, ignoring."));
                    Interlocked.CompareExchange(ref status, WorkerStatus.Stopped, WorkerStatus.Starting);
                    return;
                }

                StartWorkerThreads();

                if (bgThread != Thread.CurrentThread)
                {
                    KillBgThreadIfExists();

                    bgThread = new Thread(RunLoop)
                    {
                        IsBackground = true,
                        Name = string.Concat(typeName, " MQ Server ", Interlocked.Increment(ref bgThreadCount))
                    };

                    bgThread.Start();

                    log.Debug(string.Concat("Started Background Thread: ", bgThread.Name));
                }
                else
                {
                    log.Debug(string.Concat("Retrying RunLoop() on Thread: ", bgThread.Name));

                    RunLoop();
                }
            }
            catch (Exception ex)
            {
                if (this.ErrorHandler != null)
                {
                    this.ErrorHandler(ex);
                }
                else
                {
                    throw;
                }
            }
        }

        public void Stop()
        {
            if (Interlocked.CompareExchange(ref status, 0, 0) == WorkerStatus.Disposed)
                throw new ObjectDisposedException("MQ Host has been disposed");

            if (Interlocked.CompareExchange(ref status, WorkerStatus.Stopping, WorkerStatus.Started) == WorkerStatus.Started)
            {
                lock (msgLock)
                {
                    Interlocked.CompareExchange(ref doOperation, WorkerOperation.Stop, doOperation);
                    Monitor.Pulse(msgLock);
                }
            }

        }

        private void StartWorkerThreads()
        {
            log.Debug("Starting all SQS MQ Server worker threads...");

            foreach (var worker in workers)
            {
                try
                {
                    worker.Start();
                }
                catch (Exception ex)
                {
                    if (this.ErrorHandler != null)
                    {
                        this.ErrorHandler(ex);
                    }

                    log.Warn(string.Concat("Could not START SQS MQ worker thread: ", ex.Message));
                }
            }
        }

        private void StopWorkerThreads()
        {
            log.Debug(string.Concat("Stopping all ", typeName, " MQ Server worker threads..."));

            foreach (var worker in workers)
            {
                try
                {
                    worker.Stop();
                }
                catch (Exception ex)
                {
                    if (this.ErrorHandler != null)
                    {
                        this.ErrorHandler(ex);
                    }

                    log.Warn(string.Concat("Could not STOP ", typeName, " MQ worker thread: ", ex.Message));
                }
            }
        }

        private void KillBgThreadIfExists()
        {
            if (bgThread == null || !bgThread.IsAlive)
            {
                return;
            }

            try
            {
                if (!bgThread.Join(500))
                {
                    log.Warn(string.Concat("Interrupting previous Background Thread: ", bgThread.Name));

                    bgThread.Interrupt();

                    if (!bgThread.Join(TimeSpan.FromSeconds(3)))
                    {
                        log.Warn(string.Concat(bgThread.Name, " just wont die, so we're now aborting it..."));
                        bgThread.Abort();
                    }
                }

            }
            finally
            {
                bgThread = null;
            }
        }

        private void RunLoop()
        {
            if (Interlocked.CompareExchange(ref status, WorkerStatus.Started, WorkerStatus.Starting) != WorkerStatus.Starting)
                return;

            Interlocked.Increment(ref timesStarted);

            try
            {
                lock (msgLock)
                {
                    // Reset
                    while (Interlocked.CompareExchange(ref status, 0, 0) == WorkerStatus.Started)
                    {
                        Monitor.Wait(msgLock);
                        log.Debug("msgLock received...");

                        var op = Interlocked.CompareExchange(ref doOperation, WorkerOperation.NoOp, doOperation);

                        switch (op)
                        {
                            case WorkerOperation.Stop:
                                log.Debug("Stop Command Issued");

                                if (Interlocked.CompareExchange(ref status, WorkerStatus.Stopped, WorkerStatus.Started) != WorkerStatus.Started)
                                {
                                    Interlocked.CompareExchange(ref status, WorkerStatus.Stopped, WorkerStatus.Stopping);
                                }

                                StopWorkerThreads();
                                return;

                            case WorkerOperation.Restart:
                                log.Debug("Restart Command Issued");

                                if (Interlocked.CompareExchange(ref status, WorkerStatus.Stopped, WorkerStatus.Started) != WorkerStatus.Started)
                                {
                                    Interlocked.CompareExchange(ref status, WorkerStatus.Stopped, WorkerStatus.Stopping);
                                }

                                StopWorkerThreads();
                                StartWorkerThreads();

                                Interlocked.CompareExchange(ref status, WorkerStatus.Started, WorkerStatus.Stopped);

                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lastExMsg = ex.Message;
                Interlocked.Increment(ref noOfErrors);
                Interlocked.Increment(ref noOfContinuousErrors);

                if (Interlocked.CompareExchange(ref status, WorkerStatus.Stopped, WorkerStatus.Started) != WorkerStatus.Started)
                {
                    Interlocked.CompareExchange(ref status, WorkerStatus.Stopped, WorkerStatus.Stopping);
                }

                StopWorkerThreads();

                if (this.ErrorHandler != null)
                {
                    this.ErrorHandler(ex);
                }

                if (KeepAliveRetryAfterMs.HasValue)
                {
                    Thread.Sleep(KeepAliveRetryAfterMs.Value);
                    Start();
                }
            }

            log.Debug("Exiting RunLoop()...");
        }
    }
}