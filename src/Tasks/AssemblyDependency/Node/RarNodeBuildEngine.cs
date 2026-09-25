// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Internal;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Tasks.AssemblyDependency
{
    /// <summary>
    /// Minimal build engine implementation to buffer logging events for collection by the host.
    /// We only need to care about properties which are accessed by TaskLoggingHelper, as they will determine the
    /// contents of the created log messages.
    /// </summary>
    /// <remarks>
    /// Since RAR is often the largest source of events in MSBuild, some complexity is needed to avoid introducing
    /// overhead from excessive allocations and task context-switching. The main goals here are:
    ///
    /// - Logging an event should not block RAR, as IPC can be performed async.
    /// - Events should be batched to reduce the number of IPC calls. However, too large of a batch will add latency to
    /// the final response packet.
    /// - Pending events should be flushed when RAR completes, but before sending the result packet.
    /// - Buffers should be resusable between packets, as batches are a fixed size.
    /// - The processing task should only wake when a full packet is ready, so that each read of the event channel
    /// will immediately return a struct ValueTask.
    ///
    /// As such, two channels are used: one for events, and one for the batch size.
    ///
    /// When an event is queued, a pending count is incremented. When the count reaches the batch threshold, or the queue
    /// is explicitly flushed, the count is written to the count channel and reset.
    ///
    /// Once RAR completes, events are flushed with the remaining count.
    /// </remarks>
    internal class RarNodeBuildEngine : EngineServices, IBuildEngine10
    {
        private const int MaxBuildEventsBeforeFlush = 100;

        private readonly NodePipeServer _pipeServer;
        private readonly RarNodeBufferedLogEvents _buildEvents = new(MaxBuildEventsBeforeFlush);
        private readonly Channel<BuildEventArgs> _eventChannel;
        private readonly Channel<int> _eventCountChannel;
        private readonly SemaphoreSlim _completion = new(0, 1);
        private readonly RegisteredTaskObjectCacheBase _registeredTaskObjects = new();
        private readonly Dictionary<string, string> _globalProperties = new(StringComparer.OrdinalIgnoreCase);

        private int _pendingCount;
        private MessageImportance _minimumMessageImportance;
        private bool _isTaskInputLoggingEnabled;
        private bool _allowFailureWithoutError;

        internal RarNodeBuildEngine(NodePipeServer pipeServer)
        {
            _pipeServer = pipeServer;
            _eventChannel = Channel.CreateUnbounded<BuildEventArgs>(new UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = true,
            });
            _eventCountChannel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = true,
            });
        }

        public int LineNumberOfTaskNode { get; private set; }

        public int ColumnNumberOfTaskNode { get; private set; }

        public string? ProjectFileOfTaskNode { get; private set; }

        public EngineServices EngineServices => this;

        public override bool IsTaskInputLoggingEnabled => _isTaskInputLoggingEnabled;

        public override bool IsOutOfProcRarNodeEnabled => false;

        public bool AllowFailureWithoutError
        {
            get => _allowFailureWithoutError;
            set => _allowFailureWithoutError = value;
        }

        // RAR is executed as a dedicated node and the request does not carry a
        // task-level ContinueOnError setting. The owning build engine decides
        // how to handle the result packet, so the node must report the default.
        public bool ContinueOnError => false;

        public bool IsRunningMultipleNodes => true;

        internal void Setup(
            int lineNumberOfTaskNode,
            int columnNumberOfTaskNode,
            string? projectFileOfTaskNode,
            MessageImportance minimumMessageImportance,
            bool isTaskInputLoggingEnabled)
        {
            LineNumberOfTaskNode = lineNumberOfTaskNode;
            ColumnNumberOfTaskNode = columnNumberOfTaskNode;
            ProjectFileOfTaskNode = projectFileOfTaskNode;
            _minimumMessageImportance = minimumMessageImportance;
            _isTaskInputLoggingEnabled = isTaskInputLoggingEnabled;
        }

        public override bool LogsMessagesOfImportance(MessageImportance importance) => importance <= _minimumMessageImportance;

        public void LogErrorEvent(BuildErrorEventArgs e) => LogEvent(e);

        public void LogWarningEvent(BuildWarningEventArgs e) => LogEvent(e);

        public void LogMessageEvent(BuildMessageEventArgs e) => LogEvent(e);

        public void LogCustomEvent(CustomBuildEventArgs e) => LogEvent(e);

        /// <summary>
        /// Processes events, batching them into packets to send to the client.
        /// </summary>
        internal async Task ProcessEventsAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int eventCount = await _eventCountChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (eventCount == 0)
                    {
                        _ = _completion.Release();
                        continue;
                    }

                    // Use separate loop to take advantage of ValueTask.
                    for (int i = 0; i < eventCount; i++)
                    {
                        BuildEventArgs buildEventArgs = await _eventChannel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                        _buildEvents.EventQueue.Add(new LogMessagePacketBase(new KeyValuePair<int, BuildEventArgs>(0, buildEventArgs)));
                    }

                    await _pipeServer.WritePacketAsync(_buildEvents, cancellationToken).ConfigureAwait(false);
                    _buildEvents.EventQueue.Clear();
                }
            }
            catch (ChannelClosedException)
            {
                // This is expected when we shut down the channel.
            }
        }

        /// <summary>
        /// Flushes any pending events and waits for the processing task to send all remaining events to the client.
        /// </summary>
        internal async Task FlushEventsAsync(CancellationToken cancellationToken)
        {
            // Push the remaining number of elements to read.
            if (_pendingCount > 0)
            {
                _ = _eventCountChannel.Writer.TryWrite(_pendingCount);
                _pendingCount = 0;
            }

            // Mark the end with an empty batch and wait until all events are processed.
            _ = _eventCountChannel.Writer.TryWrite(0);
            await _completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Queues a log event to be sent to the client.
        /// These will be deferred until either a batch is full or RAR completes.
        /// </summary>
        private void LogEvent(BuildEventArgs e)
        {
            // Push the event onto the pending channel.
            _ = _eventChannel.Writer.TryWrite(e);
            _pendingCount++;

            // If we've reached the batch threshold, push the batch count.
            if (_pendingCount == MaxBuildEventsBeforeFlush)
            {
                _ = _eventCountChannel.Writer.TryWrite(_pendingCount);
                _pendingCount = 0;
            }
        }

        public void LogTelemetry(string eventName, IDictionary<string, string> properties)
        {
            var telemetry = new TelemetryEventArgs
            {
                EventName = eventName,
            };

            if (properties is not null)
            {
                foreach (KeyValuePair<string, string> property in properties)
                {
                    telemetry.Properties[property.Key] = property.Value;
                }
            }

            LogEvent(telemetry);
        }

        public bool ShouldTreatWarningAsError(string warningCode) => false;

        // The RAR endpoint deliberately does not advertise the nested-build
        // callback protocol. Returning false keeps the compatibility surface
        // deterministic until that protocol carries project requests and
        // target outputs; it must not throw a placeholder exception from a
        // task callback.
        public bool BuildProjectFile(
            string projectFileName,
            string[] targetNames,
            IDictionary globalProperties,
            IDictionary targetOutputs,
            string toolsVersion) => false;

        public bool BuildProjectFile(
            string projectFileName,
            string[] targetNames,
            IDictionary globalProperties,
            IDictionary targetOutputs) => false;

        public BuildEngineResult BuildProjectFilesInParallel(
            string[] projectFileNames,
            string[] targetNames,
            IDictionary[] globalProperties,
            IList<string>[] removeGlobalProperties,
            string[] toolsVersion,
            bool returnTargetOutputs) => new(false, null);

        public bool BuildProjectFilesInParallel(
            string[] projectFileNames,
            string[] targetNames,
            IDictionary[] globalProperties,
            IDictionary[] targetOutputsPerProject,
            string[] toolsVersion,
            bool useResultsCache,
            bool unloadProjectsOnCompletion) => false;

        public object GetRegisteredTaskObject(object key, RegisteredTaskObjectLifetime lifetime) =>
            _registeredTaskObjects.GetRegisteredTaskObject(key, lifetime);

        // This process owns no shared MSBuild scheduler state. Yield/Reacquire
        // are therefore completed synchronously instead of throwing from the
        // IBuildEngine3 compatibility surface.
        public void Reacquire() { }

        public void RegisterTaskObject(object key, object obj, RegisteredTaskObjectLifetime lifetime, bool allowEarlyCollection) =>
            _registeredTaskObjects.RegisterTaskObject(key, obj, lifetime, allowEarlyCollection);

        public object UnregisterTaskObject(object key, RegisteredTaskObjectLifetime lifetime) =>
            _registeredTaskObjects.UnregisterTaskObject(key, lifetime);

        public void Yield() { }

        public int RequestCores(int requestedCores) => requestedCores > 0 ? 1 : 0;

        public void ReleaseCores(int coresToRelease) { }

        public IReadOnlyDictionary<string, string> GetGlobalProperties() => _globalProperties;
    }
}
