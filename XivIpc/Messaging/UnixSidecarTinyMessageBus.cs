using System;
using System.IO;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using XivIpc.Internal;
using XivIpc.IO;

namespace XivIpc.Messaging
{
    internal sealed class UnixSidecarTinyMessageBus : IXivMessageBus
    {
        private const int DefaultHeartbeatIntervalMs = 2000;
        private const int DefaultHeartbeatTimeoutMs = 12000;

        private readonly string _channelName;
        private readonly int _maxPayloadBytes;
        private readonly UnixSidecarProcessManager.Lease _lease;
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly PipeReader _reader;
        private readonly PipeWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _readerTask;
        private readonly Task _heartbeatTask;
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

            _lease = UnixSidecarProcessManager.Acquire();
            (string host, int port) = ResolveEndpoint(_lease);
            _client = Connect(host, port);
            _stream = _client.GetStream();
            _reader = PipeReader.Create(_stream, new StreamPipeReaderOptions(leaveOpen: true));
            _writer = PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true));

            try
            {
                SidecarHello hello = new(
                    _channelName,
                    _maxPayloadBytes,
                    RuntimeEnvironmentDetector.GetCurrentProcessId(),
                    ResolveHeartbeatIntervalMs(),
                    ResolveHeartbeatTimeoutMs());

                SidecarProtocol.WriteHelloAsync(_writer, hello, _cts.Token).AsTask().GetAwaiter().GetResult();

                SidecarFrame readyFrame = SidecarProtocol.ReadFrameAsync(_reader, _cts.Token).AsTask().GetAwaiter().GetResult();
                if (readyFrame.Type == SidecarFrameType.Error)
                    throw new InvalidOperationException(System.Text.Encoding.UTF8.GetString(readyFrame.Payload.Span));

                if (readyFrame.Type != SidecarFrameType.Ready)
                    throw new InvalidOperationException($"Expected sidecar READY but received '{readyFrame.Type}'.");

                TinyIpcLogger.Info(
                    nameof(UnixSidecarTinyMessageBus),
                    "Connected",
                    "Connected to sidecar-backed TinyMessageBus.",
                    ("channel", _channelName),
                    ("host", host),
                    ("port", port),
                    ("maxPayloadBytes", _maxPayloadBytes));

                _readerTask = Task.Run(() => ReaderLoopAsync(_cts.Token));
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
            return WritePublishAsync(message, _cts.Token);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try { _cts.Cancel(); } catch { }
            try { TryWriteDisposeAsync().GetAwaiter().GetResult(); } catch { }
            try { _readerTask.Wait(TimeSpan.FromSeconds(1)); } catch { }
            try { _heartbeatTask.Wait(TimeSpan.FromSeconds(1)); } catch { }

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
                await SidecarProtocol.WritePublishAsync(_writer, message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            if (TinyIpcLogger.IsEnabled(TinyIpcLogLevel.Debug))
            {
                TinyIpcLogger.Debug(
                    nameof(UnixSidecarTinyMessageBus),
                    "Publish",
                    "Published message through sidecar backend.",
                    ("channel", _channelName),
                    ("bytes", message.Length),
                    ("payloadPreview", TinyIpcLogger.CreatePayloadPreview(message)));
            }
        }

        private async Task TryWriteDisposeAsync()
        {
            try
            {
                await _writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await SidecarProtocol.WriteDisposeAsync(_writer, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (Exception ex)
            {
                TinyIpcLogger.Warning(
                    nameof(UnixSidecarTinyMessageBus),
                    "DisposeSignalFailed",
                    "Failed to signal sidecar dispose.",
                    ex,
                    ("channel", _channelName));
            }
        }

        private async Task ReaderLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                try
                {
                    SidecarFrame frame = await SidecarProtocol.ReadFrameAsync(_reader, cancellationToken).ConfigureAwait(false);
                    switch (frame.Type)
                    {
                        case SidecarFrameType.Message:
                            {
                                byte[] payload = frame.Payload.ToArray();
                                try
                                {
                                    MessageReceived?.Invoke(this, new XivMessageReceivedEventArgs(payload));
                                }
                                catch (Exception ex)
                                {
                                    TinyIpcLogger.Error(
                                        nameof(UnixSidecarTinyMessageBus),
                                        "MessageHandlerFailed",
                                        "A sidecar-backed MessageReceived handler threw an exception.",
                                        ex,
                                        ("channel", _channelName));
                                }

                                break;
                            }

                        case SidecarFrameType.Error:
                            {
                                string error = System.Text.Encoding.UTF8.GetString(frame.Payload.Span);
                                TinyIpcLogger.Error(
                                    nameof(UnixSidecarTinyMessageBus),
                                    "SidecarError",
                                    "Sidecar reported an error.",
                                    null,
                                    ("channel", _channelName),
                                    ("message", error));
                                return;
                            }

                        case SidecarFrameType.Ready:
                        case SidecarFrameType.Heartbeat:
                            break;

                        default:
                            TinyIpcLogger.Warning(
                                nameof(UnixSidecarTinyMessageBus),
                                "UnexpectedFrame",
                                "Received unexpected sidecar frame.",
                                null,
                                ("channel", _channelName),
                                ("frameType", frame.Type));
                            return;
                    }
                }
                catch (EndOfStreamException)
                {
                    return;
                }
                catch (IOException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    TinyIpcLogger.Error(
                        nameof(UnixSidecarTinyMessageBus),
                        "ReaderLoopFailed",
                        "Sidecar reader loop failed.",
                        ex,
                        ("channel", _channelName));

                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
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
                        await SidecarProtocol.WriteHeartbeatAsync(_writer, cancellationToken).ConfigureAwait(false);
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
                    "Failed to send sidecar heartbeat.",
                    ex,
                    ("channel", _channelName));
            }
        }

        private static (string Host, int Port) ResolveEndpoint(UnixSidecarProcessManager.Lease lease)
        {
            string? raw = Environment.GetEnvironmentVariable("TINYIPC_SIDECAR_ENDPOINT");
            if (string.IsNullOrWhiteSpace(raw))
                return ("127.0.0.1", lease.Port);

            string[] parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
                throw new InvalidOperationException($"Invalid TINYIPC_SIDECAR_ENDPOINT value '{raw}'. Expected host:port.");

            return (parts[0], port);
        }

        private static TcpClient Connect(string host, int port)
        {
            Exception? last = null;

            for (int attempt = 0; attempt < 40; attempt++)
            {
                try
                {
                    var client = new TcpClient();
                    client.Connect(host, port);
                    client.NoDelay = true;
                    return client;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(50);
                }
            }

            throw new InvalidOperationException($"Failed to connect to sidecar endpoint {host}:{port}.", last);
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
            try { _client.Close(); } catch { }
            try { _reader.Complete(); } catch { }
            try { _writer.Complete(); } catch { }
            try { _stream.Dispose(); } catch { }
            try { _client.Dispose(); } catch { }
            try { _writeLock.Dispose(); } catch { }
            try { _cts.Dispose(); } catch { }
            try { _lease.Dispose(); } catch { }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UnixSidecarTinyMessageBus));
        }
    }
}