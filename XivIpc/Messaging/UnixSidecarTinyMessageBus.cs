using System.Collections.Concurrent;
using System.Net.Sockets;
using XivIpc.Internal;

namespace XivIpc.Messaging
{
    internal sealed class UnixSidecarTinyMessageBus : IXivMessageBus
    {
        private const int DefaultHeartbeatIntervalMs = 2000;
        private const int DefaultHeartbeatTimeoutMs = 60000;

        private readonly string _channelName;
        private readonly int _maxPayloadBytes;
        private readonly UnixSidecarProcessManager.Lease _lease;
        private readonly Socket _socket;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentQueue<byte[]> _pendingMessages = new();
        private readonly SemaphoreSlim _pendingSignal = new(0);
        private readonly Task _eventLoopTask;
        private readonly Task _dispatchLoopTask;
        private readonly Task _heartbeatTask;
        private readonly BrokeredChannelRing _ring;
        private readonly long _sessionId;
        private long _nextSequence;
        private bool _disposed;

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
            _maxPayloadBytes = maxPayloadBytes;

            try
            {
                _lease = UnixSidecarProcessManager.Acquire();
                _socket = Connect(_lease.SocketPath);

                SidecarProtocol.WriteHello(_socket, new SidecarHello(
                    _channelName,
                    _maxPayloadBytes,
                    RuntimeEnvironmentDetector.GetCurrentProcessId(),
                    ResolveHeartbeatIntervalMs(),
                    ResolveHeartbeatTimeoutMs()));

                SidecarAttachRing attach = SidecarProtocol.ReadAttachRing(_socket);
                _ring = AttachRingWithRetry(attach);
                _sessionId = attach.SessionId;
                _nextSequence = attach.StartSequence;

                SidecarFrame readyFrame = SidecarProtocol.ReadFrame(_socket);
                if (readyFrame.Type == SidecarFrameType.Error)
                    throw new InvalidOperationException(System.Text.Encoding.UTF8.GetString(readyFrame.Payload.Span));

                if (readyFrame.Type != SidecarFrameType.Ready)
                    throw new InvalidOperationException($"Expected sidecar READY but received '{readyFrame.Type}'.");

                _eventLoopTask = Task.Run(() => EventLoopAsync(_cts.Token));
                _dispatchLoopTask = Task.Run(() => DispatchLoopAsync(_cts.Token));
                _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
            }
            catch
            {
                SafeDisposeCore();
                throw;
            }
        }

        public event EventHandler<XivMessageReceivedEventArgs>? MessageReceived;

        public Task PublishAsync(byte[] message)
        {
            ArgumentNullException.ThrowIfNull(message);
            ThrowIfDisposed();

            if (message.Length > _maxPayloadBytes)
                throw new InvalidOperationException($"Message length {message.Length} exceeds configured max {_maxPayloadBytes}.");

            return WritePublishAsync(message, _cts.Token);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try { _cts.Cancel(); } catch { }
            try { TryWriteDisposeAsync(); } catch { }
            try { _socket.Dispose(); } catch { }
            try { _pendingSignal.Release(); } catch { }
            try { Task.WaitAll(new[] { _eventLoopTask, _dispatchLoopTask, _heartbeatTask }, TimeSpan.FromSeconds(2)); } catch { }

            SafeDisposeCore();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task WritePublishAsync(byte[] message, CancellationToken cancellationToken)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SidecarProtocol.WritePublish(_socket, message);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private void TryWriteDisposeAsync()
        {
            _writeLock.Wait(CancellationToken.None);
            try
            {
                SidecarProtocol.WriteDispose(_socket);
            }
            catch
            {
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task EventLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                try
                {
                    SidecarFrame frame = await Task.Run(() => SidecarProtocol.ReadFrame(_socket), cancellationToken).ConfigureAwait(false);
                    switch (frame.Type)
                    {
                        case SidecarFrameType.Notify:
                            DrainAvailableMessages();
                            break;
                        case SidecarFrameType.Error:
                            return;
                        case SidecarFrameType.Ready:
                            break;
                        default:
                            return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (IOException)
                {
                    return;
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
                    break;
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

        private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
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
                        SidecarProtocol.WriteHeartbeat(_socket);
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
                TinyIpcLogger.Warning(
                    nameof(UnixSidecarTinyMessageBus),
                    "HeartbeatFailed",
                    "Failed to send broker heartbeat.",
                    ex,
                    ("channel", _channelName));
            }
        }

        private void DrainAvailableMessages()
        {
            List<byte[]> messages = _ring.Drain(_sessionId, ref _nextSequence);
            foreach (byte[] payload in messages)
            {
                _pendingMessages.Enqueue(payload);
                _pendingSignal.Release();
            }
        }

        private static Socket Connect(string socketPath)
        {
            Exception? last = null;

            for (int attempt = 0; attempt < 40; attempt++)
            {
                try
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    socket.Connect(new UnixDomainSocketEndPoint(socketPath));
                    return socket;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(50);
                }
            }

            throw new InvalidOperationException($"Failed to connect to broker socket '{socketPath}'.", last);
        }

        private static BrokeredChannelRing AttachRingWithRetry(SidecarAttachRing attach)
        {
            Exception? last = null;

            for (int attempt = 0; attempt < 40; attempt++)
            {
                try
                {
                    return BrokeredChannelRing.Attach(attach.RingPath, attach.SlotCount, attach.SlotPayloadBytes);
                }
                catch (IOException ex)
                {
                    last = ex;
                    Thread.Sleep(25);
                }
            }

            throw new IOException($"Failed to attach broker ring '{attach.RingPath}'.", last);
        }

        private static int ResolveHeartbeatIntervalMs()
            => ResolvePositiveInt32("TINYIPC_SIDECAR_HEARTBEAT_INTERVAL_MS", DefaultHeartbeatIntervalMs);

        private static int ResolveHeartbeatTimeoutMs()
            => ResolvePositiveInt32("TINYIPC_SIDECAR_HEARTBEAT_TIMEOUT_MS", DefaultHeartbeatTimeoutMs);

        private static int ResolvePositiveInt32(string variableName, int fallback)
        {
            string? raw = Environment.GetEnvironmentVariable(variableName);
            return int.TryParse(raw, out int value) && value > 0 ? value : fallback;
        }

        private void SafeDisposeCore()
        {
            try { _ring?.Dispose(); } catch { }
            try { _socket?.Dispose(); } catch { }
            try { _pendingSignal?.Dispose(); } catch { }
            try { _writeLock?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }
            try { _lease.Dispose(); } catch { }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UnixSidecarTinyMessageBus));
        }
    }
}
