using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using XivIpc.IO;
using XivIpc.Internal;
using XivIpc.Messaging;

using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Server.NoDelay = true;
listener.Start();

int port = ((IPEndPoint)listener.LocalEndpoint).Port;
Console.Out.WriteLine($"PORT:{port}");
Console.Out.WriteLine("READY");
Console.Out.Flush();

var tasks = new ConcurrentDictionary<int, Task>();
var clientStates = new ConcurrentDictionary<int, ClientState>();

int nextClientId = 0;
long lastHeartbeatTicks = DateTimeOffset.UtcNow.UtcTicks;
int heartbeatTimeoutMs = 30000;

using var shutdownCts = new CancellationTokenSource();
Task heartbeatMonitorTask = Task.Run(() => MonitorHeartbeatAsync(shutdownCts.Token));

while (!shutdownCts.IsCancellationRequested)
{
    TcpClient client;
    try
    {
        client = await listener.AcceptTcpClientAsync(shutdownCts.Token);
        client.NoDelay = true;
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (ObjectDisposedException)
    {
        break;
    }
    catch
    {
        if (shutdownCts.IsCancellationRequested)
            break;

        continue;
    }

    int clientId = Interlocked.Increment(ref nextClientId);
    var clientCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCts.Token);

    Task task = Task.Run(async () =>
    {
        try
        {
            await HandleClientAsync(clientId, client, clientCts.Token).ConfigureAwait(false);
        }
        finally
        {
            clientStates.TryRemove(clientId, out ClientState? state);
            try { state?.Dispose(); } catch { }

            tasks.TryRemove(clientId, out _);

            try { client.Dispose(); } catch { }
            try { clientCts.Dispose(); } catch { }
        }
    }, clientCts.Token);

    tasks[clientId] = task;
}

shutdownCts.Cancel();
listener.Stop();

try
{
    await Task.WhenAll(tasks.Values).ConfigureAwait(false);
}
catch
{
}

try
{
    await heartbeatMonitorTask.ConfigureAwait(false);
}
catch
{
}

async Task HandleClientAsync(int clientId, TcpClient client, CancellationToken cancellationToken)
{
    using NetworkStream stream = client.GetStream();
    PipeReader reader = PipeReader.Create(stream);
    PipeWriter writer = PipeWriter.Create(stream);
    using var writeSync = new SemaphoreSlim(1, 1);

    ClientState? clientState = null;

    try
    {
        try
        {
            SidecarHello hello = await SidecarProtocol.ReadHelloAsync(reader, cancellationToken).ConfigureAwait(false);
            RegisterHeartbeatPolicy(hello.HeartbeatTimeoutMs);

            clientState = new ClientState(clientId, hello.Channel, hello.HeartbeatTimeoutMs, client);
            clientStates[clientId] = clientState;
            clientState.TouchHeartbeat();
            TouchHeartbeat();

            using var bus = new UnixInMemoryTinyMessageBus(hello.Channel, hello.MaxBytes);

            bus.MessageReceived += async (_, e) =>
            {
                if (clientState.IsDead || cancellationToken.IsCancellationRequested)
                    return;

                try
                {
                    await writeSync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try
                    {
                        if (clientState.IsDead || cancellationToken.IsCancellationRequested)
                            return;

                        await SidecarProtocol.WriteMessageAsync(writer, e.Message, CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        writeSync.Release();
                    }
                }
                catch
                {
                    clientState.MarkDead();
                    TryAbortClient(clientState);
                }
            };

            await writeSync.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await SidecarProtocol.WriteReadyAsync(writer, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writeSync.Release();
            }

            while (!cancellationToken.IsCancellationRequested && !clientState.IsDead)
            {
                SidecarFrame frame;
                try
                {
                    frame = await SidecarProtocol.ReadFrameAsync(reader, cancellationToken).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    clientState.MarkDead();
                    return;
                }
                catch (IOException)
                {
                    clientState.MarkDead();
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    clientState.MarkDead();
                    return;
                }

                switch (frame.Type)
                {
                    case SidecarFrameType.Publish:
                        clientState.TouchHeartbeat();
                        TouchHeartbeat();
                        await bus.PublishAsync(frame.Payload.ToArray()).ConfigureAwait(false);
                        break;

                    case SidecarFrameType.Heartbeat:
                        clientState.TouchHeartbeat();
                        TouchHeartbeat();
                        break;

                    case SidecarFrameType.Dispose:
                        clientState.TouchHeartbeat();
                        TouchHeartbeat();
                        clientState.MarkDead();
                        return;

                    default:
                        await writeSync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                        try
                        {
                            await SidecarProtocol.WriteErrorAsync(
                                writer,
                                $"Unexpected sidecar frame '{frame.Type}'.",
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        finally
                        {
                            writeSync.Release();
                        }

                        clientState.MarkDead();
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (clientState is not null)
                clientState.MarkDead();

            try
            {
                await writeSync.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    await SidecarProtocol.WriteErrorAsync(writer, ex.ToString(), CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    writeSync.Release();
                }
            }
            catch
            {
            }

            return;
        }
    }
    finally
    {
        try
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }
}

void RegisterHeartbeatPolicy(int candidateHeartbeatTimeoutMs)
{
    if (candidateHeartbeatTimeoutMs > 0)
        Interlocked.Exchange(ref heartbeatTimeoutMs, candidateHeartbeatTimeoutMs);

    TouchHeartbeat();
}

void TouchHeartbeat()
{
    Interlocked.Exchange(ref lastHeartbeatTicks, DateTimeOffset.UtcNow.UtcTicks);
}

void TryAbortClient(ClientState state)
{
    try
    {
        state.Client.Close();
    }
    catch
    {
    }

    try
    {
        state.Client.Dispose();
    }
    catch
    {
    }

    try
    {
        state.Cancel();
    }
    catch
    {
    }
}

async Task MonitorHeartbeatAsync(CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach ((int clientId, ClientState state) in clientStates)
        {
            if (state.IsDead)
                continue;

            int timeoutMs = state.HeartbeatTimeoutMs > 0
                ? state.HeartbeatTimeoutMs
                : Volatile.Read(ref heartbeatTimeoutMs);

            if (timeoutMs <= 0)
                continue;

            long observedHeartbeatTicks = Interlocked.Read(ref state.LastHeartbeatTicks);
            if (observedHeartbeatTicks <= 0)
                continue;

            TimeSpan elapsed = now - new DateTimeOffset(observedHeartbeatTicks, TimeSpan.Zero);
            if (elapsed > TimeSpan.FromMilliseconds(timeoutMs))
            {
                state.MarkDead();

                TinyIpcLogger.Warning(
                    "NativeHost",
                    "ClientHeartbeatTimeout",
                    "Disconnecting dead sidecar client after heartbeat timeout.",
                    null,
                    ("clientId", clientId),
                    ("channel", state.Channel),
                    ("timeoutMs", timeoutMs),
                    ("elapsedMs", (long)elapsed.TotalMilliseconds));

                TryAbortClient(state);
            }
        }

        long globalHeartbeatTicks = Interlocked.Read(ref lastHeartbeatTicks);
        int globalTimeoutMs = Volatile.Read(ref heartbeatTimeoutMs);
        if (globalTimeoutMs > 0)
        {
            TimeSpan elapsed = now - new DateTimeOffset(globalHeartbeatTicks, TimeSpan.Zero);
            if (elapsed > TimeSpan.FromMilliseconds(globalTimeoutMs) && clientStates.IsEmpty)
            {
                shutdownCts.Cancel();
                listener.Stop();
                return;
            }
        }
    }
}

sealed class ClientState : IDisposable
{
    private int _dead;

    public ClientState(int clientId, string channel, int heartbeatTimeoutMs, TcpClient client)
    {
        ClientId = clientId;
        Channel = channel;
        HeartbeatTimeoutMs = heartbeatTimeoutMs;
        Client = client;
        Cancellation = new CancellationTokenSource();
        LastHeartbeatTicks = DateTimeOffset.UtcNow.UtcTicks;
    }

    public int ClientId { get; }
    public string Channel { get; }
    public int HeartbeatTimeoutMs { get; }
    public TcpClient Client { get; }
    public CancellationTokenSource Cancellation { get; }
    public long LastHeartbeatTicks;

    public bool IsDead => Volatile.Read(ref _dead) != 0;

    public void TouchHeartbeat()
    {
        Interlocked.Exchange(ref LastHeartbeatTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void MarkDead()
    {
        Interlocked.Exchange(ref _dead, 1);
    }

    public void Cancel()
    {
        try
        {
            Cancellation.Cancel();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        try { Cancellation.Dispose(); } catch { }
    }
}