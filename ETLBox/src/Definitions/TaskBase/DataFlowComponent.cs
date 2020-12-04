﻿using ETLBox.ControlFlow;
using ETLBox.Exceptions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ETLBox.DataFlow
{
    /// <summary>
    /// A base class for data flow components
    /// </summary>
    [DebuggerDisplay("{TaskType}({TaskName})")]
    public abstract class DataFlowComponent : LoggableTask, IDataFlowComponent, IDataFlowLogging
    {
        #region Component properties

        /// <inheritdoc/>
        public virtual int MaxBufferSize
        {
            get
            {
                return _maxBufferSize > 0 ? _maxBufferSize : DataFlow.MaxBufferSize;
            }
            set
            {
                _maxBufferSize = value;
            }
        }

        protected int _maxBufferSize = -1;

        #endregion

        #region Linking

        /// <summary>
        /// All predecessor that are linked to this component.
        /// </summary>
        public List<DataFlowComponent> Predecessors { get; protected set; } = new List<DataFlowComponent>();

        /// <summary>
        /// All successor that this component is linked to.
        /// </summary>
        public List<DataFlowComponent> Successors { get; protected set; } = new List<DataFlowComponent>();

        /// <inheritdoc/>
        public Task Completion { get; internal set; }
        internal virtual Task BufferCompletion { get; }
        internal Task SourceOrPredecessorCompletion { get; set; }
        internal DataFlowComponent Parent { get; set; }
        internal CancellationTokenSource CancellationSource { get; set; } = new CancellationTokenSource();
        internal CancellationTokenSource CSSource { get; set; } = new CancellationTokenSource();
        internal CancellationTokenSource CSCont { get; set; } = new CancellationTokenSource();
        internal CancellationTokenSource CSCompletion { get; set; } = new CancellationTokenSource();

        protected bool WasComponentInitialized;
        protected bool ReadyForProcessing;
        protected Dictionary<DataFlowComponent, bool> WasLinked = new Dictionary<DataFlowComponent, bool>();
        internal Dictionary<DataFlowComponent, LinkPredicates> LinkPredicates = new Dictionary<DataFlowComponent, LinkPredicates>();

        protected IDataFlowSource<T> InternalLinkTo<T>(IDataFlowDestination target, object predicate = null, object voidPredicate = null)
        {
            DataFlowComponent tgt = target as DataFlowComponent;
            LinkPredicates.Add(tgt, new LinkPredicates(predicate, voidPredicate));
            this.Successors.Add(tgt);
            tgt.Predecessors.Add(this);
            var res = target as IDataFlowSource<T>;
            return res;
        }

        protected void LinkBuffersRecursively()
        {
            foreach (DataFlowComponent predecessor in Predecessors)
            {
                if (!predecessor.WasLinked.ContainsKey(this))
                {
                    LinkPredicates predicate = null;
                    LinkPredicates.TryGetValue(this, out predicate);
                    predecessor.LinkBuffers(this, predicate);
                    predecessor.WasLinked.Add(this, true);
                    predecessor.LinkBuffersRecursively();
                }
            }
            foreach (DataFlowComponent successor in Successors)
            {
                if (!WasLinked.ContainsKey(successor))
                {
                    LinkPredicates predicate = null;
                    LinkPredicates.TryGetValue(successor, out predicate);
                    LinkBuffers(successor, predicate);
                    WasLinked.Add(successor, true);
                    successor.LinkBuffersRecursively();
                }
            }
        }
        internal virtual void LinkBuffers(DataFlowComponent successor, LinkPredicates predicate)
        {
            //No linking by default
        }

        #endregion

        #region Network initialization

        internal void InitNetworkRecursively()
        {
            InitBuffersRecursively();
            LinkBuffersRecursively();
            SetCompletionTaskRecursively();
            RunErrorSourceInitializationRecursively();
        }

        protected void InitBuffersRecursively() =>
            Network.DoRecursively(this
                , comp => comp.WasComponentInitialized
                , comp => comp.InitBufferObjects()
            );

        /// <summary>
        /// Inits the underlying TPL.Dataflow buffer objects. After this, the component is ready for linking
        /// its source or target blocks.
        /// </summary>
        public void InitBufferObjects()
        {
            CheckParameter();
            InitComponent();
            WasComponentInitialized = true;
        }

        protected abstract void CheckParameter();
        protected abstract void InitComponent();

        protected void SetCompletionTaskRecursively() =>
            Network.DoRecursively(this, comp => comp.Completion != null, comp => comp.SetCompletionTask());

        protected void SetCompletionTask()
        {
            List<Task> PredecessorCompletionTasks = CollectCompletionFromPredecessors();
            if (PredecessorCompletionTasks.Count > 0)
            {
                //ComponentCompletion is manually set in DataFlowExecutableSource
                SourceOrPredecessorCompletion = Task.WhenAll(PredecessorCompletionTasks).ContinueWith(CompleteOrFaultBufferOnPredecessorCompletion, CSCont.Token);
            }
            //For sources: PredecessorCompletion is completion of SourceTask in DataFlowExecutableSource
            Completion = Task.WhenAll(SourceOrPredecessorCompletion, BufferCompletion).ContinueWith(CleanUpComponent);
        }

        private List<Task> CollectCompletionFromPredecessors()
        {
            List<Task> CompletionTasks = new List<Task>();
            foreach (DataFlowComponent pre in Predecessors)
            {
                CompletionTasks.Add(pre.SourceOrPredecessorCompletion);
                CompletionTasks.Add(pre.BufferCompletion);
            }
            return CompletionTasks;
        }

        protected void RunErrorSourceInitializationRecursively()
            => Network.DoRecursively(this, comp => comp.ReadyForProcessing, comp => comp.RunErrorSourceInit());

        protected void RunErrorSourceInit()
        {
            ErrorSource?.LetErrorSourceWaitForInput();
            ReadyForProcessing = true;
        }

        #endregion

        #region Completion tasks handling

        /// <inheritdoc/>
        public Action OnCompletion { get; set; }

        protected void CompleteOrFaultBufferOnPredecessorCompletion(Task t)
        {
            if (t.IsFaulted)
            {
                FaultBuffer(t.Exception.InnerException);
                throw t.Exception.InnerException;
                //ComponentCompletion will end as faulted
            }
            else if (t.IsCanceled)
            {
                if (Exception != null)
                    throw Exception; //This component is the source of the exception, rethrow it
                else if (CancellationSource.IsCancellationRequested) 
                    CancellationSource.Token.ThrowIfCancellationRequested(); //This component is 
                //canceled, so leave it canceled
                //else //Other branches which are not canceled or faulted will simply complete
                // CompleteBuffer();
                else //This is a successor on a different branch on cancelled tasks - cancel all successor as well
                {
                    CancellationSource.Cancel(true);
                    //while (CancellationSource.IsCancellationRequested != true) { }
                    //CancellationSource.Token.ThrowIfCancellationRequested();
                }

            }
            else
            {
                    CompleteBuffer();
                //ComponentCompletion will end as RanToCompletion
            }
        }

        internal abstract void CompleteBuffer();
        internal abstract void FaultBuffer(Exception e);

        protected void CleanUpComponent(Task t)
        {
            ErrorSource?.LetErrorSourceFinishUp();
            if (t.IsFaulted)
            {
                CleanUpOnFaulted(t.Exception.InnerException);
                throw t.Exception.InnerException;
                //The Completion will end as faulted
            }
            else if (t.IsCanceled)
            {
                CleanUpOnFaulted(null);
                 //The Completion will end as RanToCompletion, not canceled
            }
            else
            {
                CleanUpOnSuccess();
                OnCompletion?.Invoke();
                //The Completion will end as RanToCompletion
            }
        }

        protected virtual void CleanUpOnSuccess() { }

        protected virtual void CleanUpOnFaulted(Exception e) { }

        #endregion

        #region Error Handling

        /// <inheritdoc/>
        public Exception Exception { get; set; }

        /// <summary>
        /// The ErrorSource is the source block used for sending errors into the linked error flow.
        /// </summary>
        public ErrorSource ErrorSource { get; set; }

        protected IDataFlowSource<ETLBoxError> InternalLinkErrorTo(IDataFlowDestination<ETLBoxError> target)
        {
            if (ErrorSource == null)
                ErrorSource = new ErrorSource();
            ErrorSource.LinkTo(target);
            return target as IDataFlowSource<ETLBoxError>;
        }

        protected void ThrowOrRedirectError(Exception e, string erroneousData)
        {
            if (ErrorSource == null)
            {
                this.Exception = e;
                if (Parent != null) Parent.Exception = e;
                NLogger.Error(TaskName + $"throws Exception: {e.Message}", TaskType, "LOG", TaskHash, Logging.Logging.STAGE, Logging.Logging.CurrentLoadProcess?.Id);
                FaultBuffer(e);
                Parent?.FaultBuffer(e);
                CancelPredecessorsRecursivelyButStartFromSource();
                Parent?.CancelPredecessorsRecursivelyButStartFromSource();

                //CSCompletion.CancelAfter(1000);
                CSCont.CancelAfter(100);
                //CancellationSource.CancelAfter(1000);

                throw e;
            }
            else
            {
                ErrorSource.Send(e, erroneousData);
            }
        }


        protected void CancelPredecessorsRecursivelyButStartFromSource()
        {
            foreach (DataFlowComponent pre in Predecessors)
            {
                pre.CancelPredecessorsRecursivelyButStartFromSource();
                //if (pre.Exception != null && !pre.CancellationSource.IsCancellationRequested)
                //The cancellation needs to start at the source!                
                               

                pre.CSCont.Cancel(true);
                Task.Delay(10).Wait();

                pre.CancellationSource.Cancel(true);
                Task.Delay(10).Wait();

                pre.CSCompletion.Cancel(true);
                Task.Delay(10).Wait();


                pre.CSSource.Cancel(true);
                Task.Delay(10).Wait();
             
                
                
                
                //while (pre.CancellationSource.IsCancellationRequested != true) { }
                //Task.Delay(100).Wait();
            }

            //foreach (DataFlowComponent suc in Successors)
            //{
            //    if (suc.Exception != null && !suc.CancellationSource.IsCancellationRequested)
            //        suc.CancellationSource.Cancel();
            //}
        }

        #endregion

        #region Logging

        protected int? _loggingThresholdRows;

        /// <inheritdoc/>
        public virtual int? LoggingThresholdRows
        {
            get
            {
                if ((DataFlow.LoggingThresholdRows ?? 0) > 0)
                    return DataFlow.LoggingThresholdRows;
                else
                    return _loggingThresholdRows;
            }
            set
            {
                _loggingThresholdRows = value;
            }
        }

        /// <inheritdoc/>
        public int ProgressCount { get; protected set; }

        protected bool HasLoggingThresholdRows => LoggingThresholdRows != null && LoggingThresholdRows > 0;
        protected int ThresholdCount { get; set; } = 1;
        protected bool WasLoggingStarted;
        protected bool WasLoggingFinished;

        protected void NLogStartOnce()
        {
            if (!WasLoggingStarted)
                NLogStart();
            WasLoggingStarted = true;
        }
        protected void NLogFinishOnce()
        {
            if (WasLoggingStarted && !WasLoggingFinished)
                NLogFinish();
            WasLoggingFinished = true;
        }
        private void NLogStart()
        {
            if (!DisableLogging)
                NLogger.Info(TaskName, TaskType, "START", TaskHash, Logging.Logging.STAGE, Logging.Logging.CurrentLoadProcess?.Id);
        }

        private void NLogFinish()
        {
            if (!DisableLogging && HasLoggingThresholdRows)
                NLogger.Info(TaskName + $" processed {ProgressCount} records in total.", TaskType, "LOG", TaskHash, Logging.Logging.STAGE, Logging.Logging.CurrentLoadProcess?.Id);
            if (!DisableLogging)
                NLogger.Info(TaskName, TaskType, "END", TaskHash, Logging.Logging.STAGE, Logging.Logging.CurrentLoadProcess?.Id);
        }

        protected void LogProgressBatch(int rowsProcessed)
        {
            ProgressCount += rowsProcessed;
            if (!DisableLogging && HasLoggingThresholdRows && ProgressCount >= (LoggingThresholdRows * ThresholdCount))
            {
                NLogger.Info(TaskName + $" processed {ProgressCount} records.", TaskType, "LOG", TaskHash, Logging.Logging.STAGE, Logging.Logging.CurrentLoadProcess?.Id);
                ThresholdCount++;
            }
        }

        protected void LogProgress()
        {
            ProgressCount += 1;
            if (!DisableLogging && HasLoggingThresholdRows && (ProgressCount % LoggingThresholdRows == 0))
                NLogger.Info(TaskName + $" processed {ProgressCount} records.", TaskType, "LOG", TaskHash, Logging.Logging.STAGE, Logging.Logging.CurrentLoadProcess?.Id);
        }

        #endregion
    }
}
