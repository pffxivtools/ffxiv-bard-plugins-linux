using System.Collections.Concurrent;
using System.Net.Sockets;
using XivIpc.Internal;
using XivIpc.Messaging;

TinyIpcLogger.EnsureInitialized(RuntimeEnvironmentDetector.Detect());
UnixSharedStorageHelpers.EnsureBrokerAccessConfigured();

string socketPath = ResolveSocketPath();
string? socketDirectory = Path.GetDirectoryName(socketPath);
if (!string.IsNullOrWhiteSpace(socketDirectory))
{
    Directory.CreateDirectory(socketDirectory);
    UnixSharedStorageHelpers.ApplyBrokerPermissions(socketDirectory, isDirectory: true);
}

if (File.Exists(socketPath))
{
    try { File.Delete(socketPath); } catch { }
}

using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
listener.Bind(new UnixDomainSocketEndPoint(socketPath));
listener.Listen(128);

UnixSharedStorageHelpers.ApplyBrokerPermissions(socketPath, isDirectory: false);

Console.Out.WriteLine($"SOCKET:{socketPath}");
Console.Out.WriteLine("READY");
Console.Out.Flush();

var channels = new ConcurrentDictionary<string, BrokerChannel>(StringComparer.Ordinal);
var sessions = new ConcurrentDictionary<long, BrokerSession>();
long nextSessionId = 0;
long lastEmptyTicks = DateTimeOffset.UtcNow.UtcTicks;

using var shutdownCts = new CancellationTokenSource();
Task monitorTask = Task.Run(() => MonitorSessionsAsync(shutdownCts.Token));

try
{
    while (!shutdownCts.IsCancellationRequested)
    {
        Socket socket;
        try
        {
            socket = await listener.AcceptAsync(shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (ObjectDisposedException)
        {
            break;
        }

        _ = Task.Run(async () =>
        {
            BrokerSession? session = null;

            try
            {
                session = await RegisterSessionAsync(socket).ConfigureAwait(false);
                await ProcessSessionAsync(session, shutdownCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    SidecarProtocol.WriteError(socket, ex.Message);
                }
                catch
                {
                }
            }
            finally
            {
                if (session is not null)
                    RemoveSession(session);

                try { socket.Dispose(); } catch { }
            }
        }, shutdownCts.Token);
    }
}
finally
{
    shutdownCts.Cancel();

    try { listener.Dispose(); } catch { }
    try { await monitorTask.ConfigureAwait(false); } catch { }

    foreach (BrokerSession session in sessions.Values)
        session.Dispose();

    foreach (BrokerChannel channel in channels.Values)
        channel.Dispose();

    try
    {
        if (File.Exists(socketPath))
            File.Delete(socketPath);
    }
    catch
    {
    }
}

async Task<BrokerSession> RegisterSessionAsync(Socket socket)
{
    LinuxBrokerInterop.PeerCredentials peerCredentials = LinuxBrokerInterop.GetPeerCredentials(socket);
    SidecarHello hello = SidecarProtocol.ReadHello(socket);

    if (!AuthorizePeer(peerCredentials))
    {
        SidecarProtocol.WriteError(socket, "Peer is not authorized for the TinyIpc broker.");
        throw new UnauthorizedAccessException("Peer is not authorized for the TinyIpc broker.");
    }

    long sessionId = Interlocked.Increment(ref nextSessionId);
    var session = new BrokerSession(sessionId, hello.Channel, hello.HeartbeatTimeoutMs, socket, peerCredentials);

    BrokerChannel channel = channels.GetOrAdd(
        hello.Channel,
        static (channelName, state) => new BrokerChannel(channelName, state.SlotCount, state.SlotPayloadBytes, state.BrokerDirectory),
        new ChannelSeed(ResolveSlotCount(), hello.MaxBytes, socketDirectory ?? throw new InvalidOperationException("Broker socket directory was not resolved.")));

    channel.Attach(session, hello.MaxBytes);
    sessions[sessionId] = session;
    Interlocked.Exchange(ref lastEmptyTicks, 0);

    SidecarProtocol.WriteAttachRing(
        socket,
        new SidecarAttachRing(
            channel.Ring.FilePath,
            channel.Ring.SlotCount,
            channel.Ring.SlotPayloadBytes,
            channel.Ring.CaptureHead(),
            sessionId,
            channel.Ring.Length));

    SidecarProtocol.WriteReady(socket);
    await Task.CompletedTask.ConfigureAwait(false);
    return session;
}

async Task ProcessSessionAsync(BrokerSession session, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested && !session.IsDead)
    {
        SidecarFrame frame;
        try
        {
            frame = SidecarProtocol.ReadFrame(session.Socket);
        }
        catch (EndOfStreamException)
        {
            session.MarkDead();
            return;
        }
        catch (IOException)
        {
            session.MarkDead();
            return;
        }
        catch (ObjectDisposedException)
        {
            session.MarkDead();
            return;
        }

        switch (frame.Type)
        {
            case SidecarFrameType.Publish:
                session.TouchHeartbeat();
                if (channels.TryGetValue(session.Channel, out BrokerChannel? publishChannel))
                    publishChannel.Publish(session, frame.Payload.ToArray());
                break;

            case SidecarFrameType.Heartbeat:
                session.TouchHeartbeat();
                break;

            case SidecarFrameType.Dispose:
                session.MarkDead();
                return;

            default:
                SidecarProtocol.WriteError(session.Socket, $"Unexpected sidecar frame '{frame.Type}'.");
                session.MarkDead();
                return;
        }
    }

    await Task.CompletedTask.ConfigureAwait(false);
}

void RemoveSession(BrokerSession session)
{
    session.MarkDead();
    sessions.TryRemove(session.SessionId, out _);

    if (channels.TryGetValue(session.Channel, out BrokerChannel? channel))
    {
        channel.Detach(session.SessionId);
        if (channel.IsEmpty && channels.TryRemove(session.Channel, out BrokerChannel? removed))
            removed.Dispose();
    }

    if (sessions.IsEmpty)
        Interlocked.Exchange(ref lastEmptyTicks, DateTimeOffset.UtcNow.UtcTicks);

    session.Dispose();
}

async Task MonitorSessionsAsync(CancellationToken cancellationToken)
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
        foreach (BrokerSession session in sessions.Values)
        {
            if (session.IsDead)
                continue;

            int timeoutMs = session.HeartbeatTimeoutMs;
            if (timeoutMs <= 0)
                continue;

            TimeSpan elapsed = now - new DateTimeOffset(Interlocked.Read(ref session.LastHeartbeatTicks), TimeSpan.Zero);
            if (elapsed > TimeSpan.FromMilliseconds(timeoutMs))
                session.MarkDead();
        }

        foreach (BrokerSession session in sessions.Values.Where(x => x.IsDead).ToArray())
            RemoveSession(session);

        if (sessions.IsEmpty)
        {
            long emptySinceTicks = Interlocked.Read(ref lastEmptyTicks);
            if (emptySinceTicks == 0)
            {
                Interlocked.Exchange(ref lastEmptyTicks, DateTimeOffset.UtcNow.UtcTicks);
                continue;
            }

            TimeSpan emptyDuration = DateTimeOffset.UtcNow - new DateTimeOffset(emptySinceTicks, TimeSpan.Zero);
            if (emptyDuration >= ResolveIdleShutdownDelay())
            {
                shutdownCts.Cancel();
                listener.Dispose();
                return;
            }
        }
        else
        {
            Interlocked.Exchange(ref lastEmptyTicks, 0);
        }
    }
}

bool AuthorizePeer(LinuxBrokerInterop.PeerCredentials credentials)
{
    string? groupName = Environment.GetEnvironmentVariable("TINYIPC_SHARED_GROUP");
    if (string.IsNullOrWhiteSpace(groupName))
        return false;

    uint? gid = ResolveGroupId(groupName);
    if (!gid.HasValue)
        return false;

    return LinuxBrokerInterop.IsPeerInGroup(credentials, gid.Value);
}

static uint? ResolveGroupId(string groupName)
{
    foreach (string line in File.ReadLines("/etc/group"))
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] == '#')
            continue;

        string[] parts = line.Split(':');
        if (parts.Length < 3 || !string.Equals(parts[0], groupName, StringComparison.Ordinal))
            continue;

        if (uint.TryParse(parts[2], out uint gid))
            return gid;
    }

    return null;
}

static int ResolveSlotCount()
{
    string? raw = Environment.GetEnvironmentVariable("TINYIPC_BROKER_SLOT_COUNT");
    return int.TryParse(raw, out int value) && value >= 8 ? value : 256;
}

static TimeSpan ResolveIdleShutdownDelay()
{
    string? raw = Environment.GetEnvironmentVariable("TINYIPC_BROKER_IDLE_SHUTDOWN_MS");
    return int.TryParse(raw, out int value) && value >= 250
        ? TimeSpan.FromMilliseconds(value)
        : TimeSpan.FromSeconds(2);
}

static string ResolveSocketPath()
{
    string? explicitPath = Environment.GetEnvironmentVariable("TINYIPC_BROKER_SOCKET_PATH");
    if (!string.IsNullOrWhiteSpace(explicitPath))
        return ConvertWindowsPathToUnix(explicitPath);

    string? sharedDirectory = Environment.GetEnvironmentVariable("TINYIPC_BROKER_DIR")
        ?? Environment.GetEnvironmentVariable("TINYIPC_SHARED_DIR");

    if (!string.IsNullOrWhiteSpace(sharedDirectory))
        return Path.Combine(ConvertWindowsPathToUnix(sharedDirectory), "tinyipc-sidecar.sock");

    return Path.Combine("/run", "xivipc", "tinyipc-sidecar.sock");
}

static string ConvertWindowsPathToUnix(string path)
{
    string normalized = path.Replace('\\', '/');
    if (normalized.Length >= 3 &&
        char.IsLetter(normalized[0]) &&
        normalized[1] == ':' &&
        normalized[2] == '/')
    {
        char drive = char.ToUpperInvariant(normalized[0]);
        string remainder = normalized[3..];
        return drive == 'Z' ? "/" + remainder : $"/mnt/{char.ToLowerInvariant(drive)}/{remainder}";
    }

    return normalized;
}

sealed class BrokerChannel : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, BrokerSession> _sessions = new();
    private readonly string _ringPath;

    public BrokerChannel(string channelName, int slotCount, int slotPayloadBytes, string brokerDirectory)
    {
        ChannelName = channelName;
        _ringPath = UnixSharedStorageHelpers.BuildSharedFilePath(brokerDirectory, channelName, "broker-ring");
        Ring = BrokeredChannelRing.Create(_ringPath, slotCount, slotPayloadBytes);
    }

    public string ChannelName { get; }
    public BrokeredChannelRing Ring { get; }
    public bool IsEmpty
    {
        get
        {
            lock (_gate)
                return _sessions.Count == 0;
        }
    }

    public void Attach(BrokerSession session, int requestedMaxPayloadBytes)
    {
        lock (_gate)
        {
            if (requestedMaxPayloadBytes > Ring.SlotPayloadBytes)
            {
                throw new InvalidOperationException(
                    $"Existing brokered ring for channel '{ChannelName}' has payload capacity {Ring.SlotPayloadBytes}, which is smaller than requested {requestedMaxPayloadBytes}.");
            }

            _sessions[session.SessionId] = session;
        }
    }

    public void Detach(long sessionId)
    {
        lock (_gate)
            _sessions.Remove(sessionId);
    }

    public void Publish(BrokerSession sender, byte[] payload)
    {
        BrokerSession[] recipients;

        lock (_gate)
        {
            Ring.Publish(sender.SessionId, payload);
            recipients = _sessions.Values.Where(x => !x.IsDead).ToArray();
        }

        foreach (BrokerSession recipient in recipients)
        {
            try
            {
                SidecarProtocol.WriteNotify(recipient.Socket);
            }
            catch
            {
                recipient.MarkDead();
            }
        }
    }

    public void Dispose()
    {
        Ring.Dispose();
        try
        {
            string runtimePath = UnixSharedStorageHelpers.ConvertPathForCurrentRuntime(_ringPath);
            if (File.Exists(runtimePath))
                File.Delete(runtimePath);
        }
        catch
        {
        }
    }
}

sealed class BrokerSession : IDisposable
{
    private int _dead;

    public BrokerSession(long sessionId, string channel, int heartbeatTimeoutMs, Socket socket, LinuxBrokerInterop.PeerCredentials peerCredentials)
    {
        SessionId = sessionId;
        Channel = channel;
        HeartbeatTimeoutMs = heartbeatTimeoutMs > 0 ? heartbeatTimeoutMs : 60000;
        Socket = socket;
        PeerCredentials = peerCredentials;
        LastHeartbeatTicks = DateTimeOffset.UtcNow.UtcTicks;
    }

    public long SessionId { get; }
    public string Channel { get; }
    public int HeartbeatTimeoutMs { get; }
    public Socket Socket { get; }
    public LinuxBrokerInterop.PeerCredentials PeerCredentials { get; }
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

    public void Dispose()
    {
        try { Socket.Dispose(); } catch { }
    }
}

readonly record struct ChannelSeed(int SlotCount, int SlotPayloadBytes, string BrokerDirectory);
