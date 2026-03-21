using System.Collections.Concurrent;
using System.Net.Sockets;
using XivIpc.Internal;

namespace XivIpc.Messaging
{
    internal sealed class UnixSidecarTinyMessageBus : IXivMessageBus
    {
        private const int DefaultHeartbeatIntervalMs = 2000;
        private const int DefaultHeartbeatTimeoutMs = 60000;
        private const int MaxReconnectQueuedMessages = 64;
        private static readonly TimeSpan[] ReconnectBackoffSchedule =
        {
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5)
        };

        private enum ConnectionState
        {
            Connecting,
            Connected,
            Reconnecting,
            Disposed
        }

        private readonly string _channelName;
        private readonly int _requestedBufferBytes;
        private readonly Guid _clientInstanceId;
        private readonly UnixSidecarProcessManager.RuntimeSettings _runtimeSettings;
        private readonly object _stateGate = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly ConcurrentQueue<byte[]> _pendingMessages = new();
        private readonly SemaphoreSlim _pendingSignal = new(0);
        private readonly ConcurrentQueue<byte[]> _outboundMessages = new();
        private readonly SemaphoreSlim _outboundSignal = new(0);
        private readonly SemaphoreSlim _reconnectSignal = new(0);
        private readonly Task _dispatchLoopTask;
        private readonly Task _sendLoopTask;
        private readonly Task _connectionLoopTask;

        private Socket? _socket;
        private BrokeredChannelJournal? _journal;
        private UnixSidecarProcessManager.Lease? _lease;
        private CancellationTokenSource? _connectionCts;
        private Task? _eventLoopTask;
        private Task? _heartbeatTask;
        private TaskCompletionSource<bool> _connectedTcs = CreateConnectedTcs();
        private ConnectionState _connectionState;
        private bool _reconnectPending;
        private bool _disposed;
        private long _sessionId;
        private long _nextSequence;
        private int _effectiveMaxPayloadBytes;
        private JournalSizing _effectiveSizing;
        private long _drainedMessageCount;
        private long _retainedTailCatchupCount;
        private long _maxLagSeen;
        private long _lastObservedHead;
        private long _lastObservedTail;
        private long _queuedPublishBytes;
        private long _queuedPublishCount;

        public UnixSidecarTinyMessageBus(ChannelInfo channelInfo)
            : this(channelInfo.Name, checked((int)channelInfo.Size))
        {
        }

        public UnixSidecarTinyMessageBus(string channelName, int maxPayloadBytes)
        {
            if (string.IsNullOrWhiteSpace(channelName))
                throw new ArgumentException("Channel name must be provided.", nameof(channelName));

            if (maxPayloadBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));

            _channelName = channelName;
            _requestedBufferBytes = maxPayloadBytes;
            _clientInstanceId = Guid.NewGuid();
            _runtimeSettings = UnixSidecarProcessManager.CaptureSettings();
            _effectiveSizing = JournalSizingPolicy.Compute(_requestedBufferBytes, BrokeredChannelJournal.HeaderBytes);
            _effectiveMaxPayloadBytes = _effectiveSizing.MaxPayloadBytes;
            _connectionState = ConnectionState.Connecting;

            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            AppDomain.CurrentDomain.DomainUnload += OnProcessExit;

            _dispatchLoopTask = Task.Run(() => DispatchLoopAsync(_lifetimeCts.Token));
            _sendLoopTask = Task.Run(() => SendLoopAsync(_lifetimeCts.Token));
            _connectionLoopTask = Task.Run(() => ConnectionLoopAsync(_lifetimeCts.Token));

            RequestReconnect("InitialConnectScheduled", "Scheduled initial sidecar connection in the background.", null);
        }

        public event EventHandler<XivMessageReceivedEventArgs>? MessageReceived;

        public Task PublishAsync(byte[] message)
        {
            ArgumentNullException.ThrowIfNull(message);
            ThrowIfDisposed();

            int allowedBytes = Volatile.Read(ref _effectiveMaxPayloadBytes);
            if (message.Length > allowedBytes)
            {
                throw new InvalidOperationException(
                    $"Message length {message.Length} exceeds the configured per-message capacity of {allowedBytes} bytes. " +
                    $"requestedBufferBytes={_requestedBufferBytes}, " +
                    $"effectiveBudgetBytes={_effectiveSizing.BudgetBytes}.");
            }

            EnqueueOutbound(message);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            lock (_stateGate)
            {
                _connectionState = ConnectionState.Disposed;
                _connectedTcs.TrySetCanceled();
            }

            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            AppDomain.CurrentDomain.DomainUnload -= OnProcessExit;

            try { _lifetimeCts.Cancel(); } catch { }
            try { _reconnectSignal.Release(); } catch { }
            try { _outboundSignal.Release(); } catch { }
            try { _pendingSignal.Release(); } catch { }

            TeardownConnection(sendDispose: true);

            try { Task.WaitAll(new[] { _connectionLoopTask, _sendLoopTask, _dispatchLoopTask }, TimeSpan.FromSeconds(2)); } catch { }

            TinyIpcLogger.Info(
                nameof(UnixSidecarTinyMessageBus),
                "BrokeredDrainSummary",
                "Completed broker-backed bus lifetime summary.",
                ("channel", _channelName),
                ("requestedBufferBytes", _requestedBufferBytes),
                ("effectiveBudgetBytes", _effectiveSizing.BudgetBytes),
                ("maxPayloadBytes", _effectiveMaxPayloadBytes),
                ("drainedMessageCount", Interlocked.Read(ref _drainedMessageCount)),
                ("retainedTailCatchupCount", Interlocked.Read(ref _retainedTailCatchupCount)),
                ("maxLagSeen", Interlocked.Read(ref _maxLagSeen)),
                ("lastObservedHead", Interlocked.Read(ref _lastObservedHead)),
                ("lastObservedTail", Interlocked.Read(ref _lastObservedTail)),
                ("queuedPublishCount", Interlocked.Read(ref _queuedPublishCount)),
                ("queuedPublishBytes", Interlocked.Read(ref _queuedPublishBytes)));

            try { _pendingSignal.Dispose(); } catch { }
            try { _outboundSignal.Dispose(); } catch { }
            try { _reconnectSignal.Dispose(); } catch { }
            try { _writeLock.Dispose(); } catch { }
            try { _lifetimeCts.Dispose(); } catch { }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        internal async Task WaitForConnectedForDiagnosticsAsync(TimeSpan timeout)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            cts.CancelAfter(timeout);
            await WaitUntilConnectedAsync(cts.Token).ConfigureAwait(false);
        }

        private async Task ConnectionLoopAsync(CancellationToken cancellationToken)
        {
            int attemptIndex = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _reconnectSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                lock (_stateGate)
                    _reconnectPending = false;

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await ConnectAndAttachAsync(cancellationToken).ConfigureAwait(false);
                        attemptIndex = 0;
                        break;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (_disposed)
                            return;

                        TimeSpan delay = ComputeReconnectDelay(attemptIndex++);
                        TinyIpcLogger.Warning(
                            nameof(UnixSidecarTinyMessageBus),
                            "ReconnectAttemptFailed",
                            "Background sidecar connect or reconnect attempt failed; retrying.",
                            ex,
                            ("channel", _channelName),
                            ("attemptIndex", attemptIndex),
                            ("delayMs", (int)delay.TotalMilliseconds));

                        try
                        {
                            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }
        }

        private async Task ConnectAndAttachAsync(CancellationToken cancellationToken)
        {
            lock (_stateGate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(UnixSidecarTinyMessageBus));

                _connectionState = _sessionId == 0 ? ConnectionState.Connecting : ConnectionState.Reconnecting;
            }

            UnixSidecarProcessManager.Lease lease = default;
            Socket? socket = null;
            BrokeredChannelJournal? journal = null;
            CancellationTokenSource? connectionCts = null;

            try
            {
                lease = UnixSidecarProcessManager.Acquire(_runtimeSettings);
                socket = ConnectOnce(lease.SocketPath);

                SidecarProtocol.WriteHello(socket, new SidecarHello(
                    _channelName,
                    _requestedBufferBytes,
                    RuntimeEnvironmentDetector.GetCurrentProcessId(),
                    ResolveHeartbeatIntervalMs(),
                    ResolveHeartbeatTimeoutMs(),
                    _clientInstanceId));

                SidecarFrame attachFrame = SidecarProtocol.ReadFrame(socket);
                LogAttachFrameReceived(attachFrame);
                SidecarAttachRing attach = DecodeAttachFrame(attachFrame);
                journal = BrokeredChannelJournal.Attach(attach.RingPath, attach.SlotCount, attach.SlotPayloadBytes, attach.RingLength, ResolveMinMessageAgeMs());
                JournalSizing sizing = JournalSizingPolicy.Compute(_requestedBufferBytes, BrokeredChannelJournal.HeaderBytes);

                SidecarFrame readyFrame = SidecarProtocol.ReadFrame(socket);
                if (readyFrame.Type == SidecarFrameType.Error)
                    throw new InvalidOperationException(System.Text.Encoding.UTF8.GetString(readyFrame.Payload.Span));

                if (readyFrame.Type != SidecarFrameType.Ready)
                    throw new InvalidOperationException($"Expected sidecar READY but received '{readyFrame.Type}'.");

                connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Socket eventSocket = socket;
                BrokeredChannelJournal eventJournal = journal;
                CancellationTokenSource eventConnectionCts = connectionCts;

                lock (_stateGate)
                {
                    if (_disposed)
                        throw new ObjectDisposedException(nameof(UnixSidecarTinyMessageBus));

                    _lease = lease;
                    _socket = socket;
                    _journal = journal;
                    _connectionCts = connectionCts;
                    _effectiveSizing = sizing;
                    _effectiveMaxPayloadBytes = attach.SlotPayloadBytes;
                    _sessionId = attach.SessionId;
                    _nextSequence = attach.StartSequence;
                    _connectionState = ConnectionState.Connected;
                    _connectedTcs.TrySetResult(true);
                    _eventLoopTask = Task.Run(() => EventLoopAsync(eventSocket, eventJournal, attach.SessionId, eventConnectionCts.Token));
                    _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(eventSocket, eventConnectionCts.Token));
                }

                TinyIpcLogger.Info(
                    nameof(UnixSidecarTinyMessageBus),
                    "ReconnectSucceeded",
                    "Connected or reconnected to the sidecar broker.",
                    ("channel", _channelName),
                    ("sessionId", attach.SessionId),
                    ("requestedBufferBytes", _requestedBufferBytes),
                    ("effectiveBudgetBytes", sizing.BudgetBytes),
                    ("maxPayloadBytes", attach.SlotPayloadBytes),
                    ("socketPath", lease.SocketPath));

                lease = default;
                socket = null;
                journal = null;
                connectionCts = null;
                await Task.CompletedTask.ConfigureAwait(false);
            }
            finally
            {
                try { connectionCts?.Dispose(); } catch { }
                try { journal?.Dispose(); } catch { }
                try { socket?.Dispose(); } catch { }
                try { lease.Dispose(); } catch { }
            }
        }

        private async Task SendLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _outboundSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                while (!cancellationToken.IsCancellationRequested && _outboundMessages.TryPeek(out byte[]? payload))
                {
                    await WaitUntilConnectedAsync(cancellationToken).ConfigureAwait(false);
                    if (_disposed)
                        return;

                    Socket? socket = GetCurrentSocket();
                    if (socket is null)
                        break;

                    await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (!ReferenceEquals(socket, GetCurrentSocket()))
                            continue;

                        SidecarProtocol.WritePublish(socket, payload);
                        if (_outboundMessages.TryDequeue(out byte[]? dequeued))
                        {
                            Interlocked.Decrement(ref _queuedPublishCount);
                            Interlocked.Add(ref _queuedPublishBytes, -dequeued.Length);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        HandleConnectionLost(socket, "DisconnectedDuringPublish", ex);
                        break;
                    }
                    finally
                    {
                        _writeLock.Release();
                    }
                }
            }
        }

        private async Task DispatchLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _pendingSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                while (!_disposed && _pendingMessages.TryDequeue(out byte[]? payload))
                {
                    try
                    {
                        MessageReceived?.Invoke(this, new XivMessageReceivedEventArgs(payload));
                    }
                    catch (Exception ex)
                    {
                        TinyIpcLogger.Error(
                            nameof(UnixSidecarTinyMessageBus),
                            "MessageHandlerFailed",
                            "A broker-backed MessageReceived handler threw an exception.",
                            ex,
                            ("channel", _channelName));
                    }
                }
            }
        }

        private async Task EventLoopAsync(Socket socket, BrokeredChannelJournal journal, long sessionId, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                try
                {
                    SidecarFrame frame = await Task.Run(() => SidecarProtocol.ReadFrame(socket), cancellationToken).ConfigureAwait(false);
                    switch (frame.Type)
                    {
                        case SidecarFrameType.Notify:
                            DrainAvailableMessages(journal, sessionId);
                            break;
                        case SidecarFrameType.Error:
                            HandleConnectionLost(socket, "BrokerReturnedErrorFrame", new InvalidOperationException(System.Text.Encoding.UTF8.GetString(frame.Payload.Span)));
                            return;
                        case SidecarFrameType.Ready:
                            break;
                        default:
                            HandleConnectionLost(socket, "BrokerReturnedUnexpectedFrame", new InvalidOperationException($"Unexpected sidecar frame '{frame.Type}'."));
                            return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    HandleConnectionLost(socket, "DisconnectedWhileReading", ex);
                    return;
                }
            }
        }

        private async Task HeartbeatLoopAsync(Socket socket, CancellationToken cancellationToken)
        {
            TimeSpan interval = TimeSpan.FromMilliseconds(ResolveHeartbeatIntervalMs());
            using var timer = new PeriodicTimer(interval);

            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (_disposed)
                        return;

                    await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (!ReferenceEquals(socket, GetCurrentSocket()))
                            return;

                        SidecarProtocol.WriteHeartbeat(socket);
                    }
                    finally
                    {
                        _writeLock.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                HandleConnectionLost(socket, "HeartbeatFailed", ex);
            }
        }

        private void DrainAvailableMessages(BrokeredChannelJournal journal, long sessionId)
        {
            BrokeredJournalDrainResult result = journal.Drain(_clientInstanceId, ref _nextSequence);
            long lagBefore = Math.Max(0, result.LagBefore);
            Interlocked.Exchange(ref _lastObservedHead, result.HeadSequenceObserved);
            Interlocked.Exchange(ref _lastObservedTail, result.TailSequenceObserved);
            UpdateMaxLag(lagBefore);

            if (TinyIpcLogger.IsEnabled(TinyIpcLogLevel.Debug))
            {
                TinyIpcLogger.Debug(
                    nameof(UnixSidecarTinyMessageBus),
                    "NotifyDrainedMessages",
                    "Drained available broker-backed messages for the current sidecar session.",
                    ("channel", _channelName),
                    ("sessionId", sessionId),
                    ("drainedCount", result.Messages.Count),
                    ("headObserved", result.HeadSequenceObserved),
                    ("tailObserved", result.TailSequenceObserved),
                    ("nextSequenceBefore", result.NextSequenceBefore),
                    ("nextSequenceAfter", result.NextSequenceAfter),
                    ("lagBefore", lagBefore),
                    ("caughtUpToTail", result.CaughtUpToTail));
            }

            if (result.CaughtUpToTail)
            {
                Interlocked.Increment(ref _retainedTailCatchupCount);
                TinyIpcLogger.Warning(
                    nameof(UnixSidecarTinyMessageBus),
                    "SubscriberCaughtUpToRetainedTail",
                    "A broker-backed subscriber fell behind the retained journal tail and was advanced.",
                    null,
                    ("channel", _channelName),
                    ("headObserved", result.HeadSequenceObserved),
                    ("tailObserved", result.TailSequenceObserved),
                    ("nextSequenceBefore", result.NextSequenceBefore),
                    ("nextSequenceAfter", result.NextSequenceAfter),
                    ("lagBefore", lagBefore),
                    ("retainedBytes", result.RetainedBytes));
            }

            foreach (byte[] payload in result.Messages)
            {
                _pendingMessages.Enqueue(payload);
                _pendingSignal.Release();
                Interlocked.Increment(ref _drainedMessageCount);
            }
        }

        private void UpdateMaxLag(long lag)
        {
            long current = Volatile.Read(ref _maxLagSeen);
            while (lag > current)
            {
                long observed = Interlocked.CompareExchange(ref _maxLagSeen, lag, current);
                if (observed == current)
                {
                    if (lag >= Math.Max(1, _effectiveSizing.BudgetBytes * 3L / 4L))
                    {
                        TinyIpcLogger.Info(
                            nameof(UnixSidecarTinyMessageBus),
                            "SubscriberHighLagObserved",
                            "Observed a high broker-backed subscriber lag.",
                            ("channel", _channelName),
                            ("lag", lag),
                            ("effectiveBudgetBytes", _effectiveSizing.BudgetBytes),
                            ("maxPayloadBytes", _effectiveMaxPayloadBytes));
                    }

                    return;
                }

                current = observed;
            }
        }

        private async Task WaitUntilConnectedAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Task waitTask;
                lock (_stateGate)
                {
                    if (_disposed)
                        return;

                    if (_connectionState == ConnectionState.Connected && _socket is not null)
                        return;

                    waitTask = _connectedTcs.Task;
                }

                await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private void EnqueueOutbound(byte[] message)
        {
            byte[] copy = new byte[message.Length];
            Buffer.BlockCopy(message, 0, copy, 0, message.Length);
            _outboundMessages.Enqueue(copy);
            Interlocked.Increment(ref _queuedPublishCount);
            Interlocked.Add(ref _queuedPublishBytes, copy.Length);

            long maxQueuedBytes = Math.Min(8L * 1024L * 1024L, _effectiveSizing.BudgetBytes);
            while (Interlocked.Read(ref _queuedPublishCount) > MaxReconnectQueuedMessages ||
                   Interlocked.Read(ref _queuedPublishBytes) > maxQueuedBytes)
            {
                if (!_outboundMessages.TryDequeue(out byte[]? dropped))
                    break;

                Interlocked.Decrement(ref _queuedPublishCount);
                Interlocked.Add(ref _queuedPublishBytes, -dropped.Length);
                TinyIpcLogger.Warning(
                    nameof(UnixSidecarTinyMessageBus),
                    "QueuedPublishDropped",
                    "Dropped an oldest queued publish while reconnecting because the reconnect queue was full.",
                    null,
                    ("channel", _channelName),
                    ("droppedMessageLength", dropped.Length),
                    ("queuedPublishCount", Interlocked.Read(ref _queuedPublishCount)),
                    ("queuedPublishBytes", Interlocked.Read(ref _queuedPublishBytes)),
                    ("maxQueuedBytes", maxQueuedBytes));
            }

            try { _outboundSignal.Release(); } catch { }
        }

        private void RequestReconnect(string eventName, string message, Exception? exception)
        {
            if (_disposed)
                return;

            lock (_stateGate)
            {
                if (_disposed || _connectionState == ConnectionState.Disposed)
                    return;

                if (_connectionState != ConnectionState.Connected)
                    _connectionState = ConnectionState.Reconnecting;

                if (!_reconnectPending)
                {
                    _reconnectPending = true;
                    _reconnectSignal.Release();
                }
            }

            TinyIpcLogger.Warning(
                nameof(UnixSidecarTinyMessageBus),
                eventName,
                message,
                exception,
                ("channel", _channelName),
                ("queuedPublishCount", Interlocked.Read(ref _queuedPublishCount)),
                ("queuedPublishBytes", Interlocked.Read(ref _queuedPublishBytes)));
        }

        private void HandleConnectionLost(Socket socket, string eventName, Exception ex)
        {
            lock (_stateGate)
            {
                if (!ReferenceEquals(socket, _socket) || _disposed)
                    return;
            }

            TeardownConnection(sendDispose: false);
            RequestReconnect(eventName, "Lost the current sidecar connection; scheduling reconnect.", ex);
        }

        private void TeardownConnection(bool sendDispose)
        {
            Socket? socket;
            BrokeredChannelJournal? journal;
            UnixSidecarProcessManager.Lease? lease;
            CancellationTokenSource? connectionCts;

            lock (_stateGate)
            {
                socket = _socket;
                journal = _journal;
                lease = _lease;
                connectionCts = _connectionCts;
                _socket = null;
                _journal = null;
                _lease = null;
                _connectionCts = null;

                if (!_disposed)
                {
                    _connectionState = ConnectionState.Reconnecting;
                    _connectedTcs = CreateConnectedTcs();
                }
            }

            try { connectionCts?.Cancel(); } catch { }

            if (sendDispose && socket is not null)
            {
                try
                {
                    _writeLock.Wait(CancellationToken.None);
                    SidecarProtocol.WriteDispose(socket);
                }
                catch
                {
                }
                finally
                {
                    try { _writeLock.Release(); } catch { }
                }
            }

            try { socket?.Shutdown(SocketShutdown.Both); } catch { }
            try { socket?.Dispose(); } catch { }
            try { journal?.Dispose(); } catch { }
            try { lease?.Dispose(); } catch { }
            try { connectionCts?.Dispose(); } catch { }
        }

        private Socket? GetCurrentSocket()
        {
            lock (_stateGate)
                return _socket;
        }

        private static TaskCompletionSource<bool> CreateConnectedTcs()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static Socket ConnectOnce(string socketPath)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Connect(new UnixDomainSocketEndPoint(socketPath));
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private SidecarAttachRing DecodeAttachFrame(SidecarFrame frame)
        {
            if (frame.Type == SidecarFrameType.Error)
            {
                string message = frame.Payload.Length == 0
                    ? "Broker attach failed."
                    : System.Text.Encoding.UTF8.GetString(frame.Payload.Span);
                TinyIpcLogger.Warning(
                    nameof(UnixSidecarTinyMessageBus),
                    "AttachRejectedByBroker",
                    "Broker rejected sidecar attach.",
                    null,
                    ("channel", _channelName),
                    ("payloadLength", frame.Payload.Length),
                    ("brokerError", message));
                throw new InvalidOperationException(message);
            }

            try
            {
                return SidecarProtocol.DecodeAttachRing(frame);
            }
            catch (Exception ex)
            {
                TinyIpcLogger.Warning(
                    nameof(UnixSidecarTinyMessageBus),
                    "AttachRingDecodeFailed",
                    "Failed to decode broker ATTACH_RING frame.",
                    ex,
                    ("channel", _channelName),
                    ("frameType", frame.Type),
                    ("payloadLength", frame.Payload.Length),
                    ("payloadPreview", BuildPayloadPreview(frame.Payload.Span)));
                throw;
            }
        }

        private void LogAttachFrameReceived(SidecarFrame frame)
        {
            TinyIpcLogger.Info(
                nameof(UnixSidecarTinyMessageBus),
                "AttachRingFrameReceived",
                "Received broker attach frame.",
                ("channel", _channelName),
                ("frameType", frame.Type),
                ("payloadLength", frame.Payload.Length),
                ("payloadPreview", BuildPayloadPreview(frame.Payload.Span)));
        }

        private static TimeSpan ComputeReconnectDelay(int attemptIndex)
        {
            TimeSpan baseline = ReconnectBackoffSchedule[Math.Min(attemptIndex, ReconnectBackoffSchedule.Length - 1)];
            int jitterMs = Random.Shared.Next(0, 251);
            return baseline + TimeSpan.FromMilliseconds(jitterMs);
        }

        private static int ResolveHeartbeatIntervalMs()
            => ResolvePositiveInt32("TINYIPC_SIDECAR_HEARTBEAT_INTERVAL_MS", DefaultHeartbeatIntervalMs);

        private static int ResolveHeartbeatTimeoutMs()
            => ResolvePositiveInt32("TINYIPC_SIDECAR_HEARTBEAT_TIMEOUT_MS", DefaultHeartbeatTimeoutMs);

        private static long ResolveMinMessageAgeMs()
        {
            string? raw = TinyIpcEnvironment.GetEnvironmentVariable(TinyIpcEnvironment.MessageTtlMs);
            return long.TryParse(raw, out long value) && value >= 0 ? value : 1_000L;
        }

        private static int ResolvePositiveInt32(string variableName, int fallback)
        {
            string? raw = TinyIpcEnvironment.GetEnvironmentVariable(variableName);
            return int.TryParse(raw, out int value) && value > 0 ? value : fallback;
        }

        private static string BuildPayloadPreview(ReadOnlySpan<byte> payload)
        {
            int count = Math.Min(payload.Length, 32);
            return count == 0 ? string.Empty : Convert.ToHexString(payload[..count]);
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            try
            {
                Dispose();
            }
            catch
            {
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UnixSidecarTinyMessageBus));
        }
    }
}
