using System.Collections.Concurrent;
using System.Net.Sockets;
using XivIpc.Internal;
using XivIpc.Messaging;

TinyIpcLogger.EnsureInitialized(RuntimeEnvironmentDetector.Detect());
TinyIpcProcessStamp brokerStamp = TinyIpcProcessStamp.Create(typeof(BrokerStateSnapshot));
TinyIpcLogger.Info(
    "NativeHost",
    "BrokerProcessStamp",
    "Resolved TinyIpc native broker process stamp.",
    ("assemblyPath", brokerStamp.AssemblyPath),
    ("assemblyName", brokerStamp.AssemblyName),
    ("informationalVersion", brokerStamp.InformationalVersion),
    ("fileVersion", brokerStamp.FileVersion),
    ("sha256", brokerStamp.Sha256),
    ("processPath", brokerStamp.ProcessPath),
    ("processSha256", brokerStamp.ProcessSha256));
TinyIpcLogger.Info(
    "NativeHost",
    "BrokerStartupObservedEnvironment",
    "Observed native broker startup environment before path normalization.",
    ("launchId", Environment.GetEnvironmentVariable("TINYIPC_LAUNCH_ID") ?? string.Empty),
    ("sharedDir", Environment.GetEnvironmentVariable("TINYIPC_SHARED_DIR") ?? string.Empty),
    ("brokerDir", Environment.GetEnvironmentVariable("TINYIPC_BROKER_DIR") ?? string.Empty),
    ("logDir", Environment.GetEnvironmentVariable("TINYIPC_LOG_DIR") ?? string.Empty),
    ("sharedGroup", Environment.GetEnvironmentVariable("TINYIPC_SHARED_GROUP") ?? string.Empty),
    ("brokerSocketPath", Environment.GetEnvironmentVariable("TINYIPC_BROKER_SOCKET_PATH") ?? string.Empty),
    ("backend", Environment.GetEnvironmentVariable("TINYIPC_BUS_BACKEND") ?? string.Empty));
UnixSharedStorageHelpers.EnsureBrokerAccessConfigured();

string socketPath = ResolveSocketPath();
string? socketDirectory = Path.GetDirectoryName(socketPath);
if (!string.IsNullOrWhiteSpace(socketDirectory))
{
    Directory.CreateDirectory(socketDirectory);
    UnixSharedStorageHelpers.ApplyBrokerPermissions(socketDirectory, isDirectory: true);
}

using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
if (!TryBindListener(listener, socketPath))
    return;

listener.Listen(128);

UnixSharedStorageHelpers.ApplyBrokerPermissions(socketPath, isDirectory: false);

BrokerStateSnapshot brokerState = BrokerStateSnapshot.CreateNew(socketPath);
string brokerStatePath = BrokerStateSnapshot.ResolvePath(socketPath);
BrokerStateSnapshot.Write(brokerStatePath, brokerState);

Console.Out.WriteLine($"SOCKET:{socketPath}");
Console.Out.WriteLine("READY");
Console.Out.Flush();

var channels = new ConcurrentDictionary<string, BrokerChannel>(StringComparer.Ordinal);
var sessions = new ConcurrentDictionary<long, BrokerSession>();
long nextSessionId = 0;
long lastEmptyTicks = DateTimeOffset.UtcNow.UtcTicks;
var stateGate = new object();

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
                TinyIpcLogger.Error(
                    "NativeHost",
                    "SessionProcessingFailed",
                    "Broker session processing failed.",
                    ex,
                    ("hasSession", session is not null),
                    ("sessionId", session?.SessionId ?? 0),
                    ("channel", session?.Channel ?? string.Empty),
                    ("peerUid", session?.PeerCredentials.Uid ?? 0),
                    ("peerGid", session?.PeerCredentials.Gid ?? 0));
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
        BrokerStateSnapshot? activeState = BrokerStateSnapshot.TryRead(brokerStatePath);
        if (activeState is not null && string.Equals(activeState.InstanceId, brokerState.InstanceId, StringComparison.Ordinal) && File.Exists(socketPath))
            File.Delete(socketPath);
    }
    catch
    {
    }

    BrokerStateSnapshot.DeleteIfOwned(brokerStatePath, brokerState.InstanceId);
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

    RingSizing requestedSizing = RingSizingPolicy.Compute(hello.MaxBytes, ResolveSlotCount(), BrokeredChannelRing.HeaderBytes, BrokeredChannelRing.SlotHeaderBytes);
    long sessionId = Interlocked.Increment(ref nextSessionId);
    var session = new BrokerSession(sessionId, hello.Channel, hello.HeartbeatTimeoutMs, socket, peerCredentials);

    try
    {
        BrokerChannel channel = channels.GetOrAdd(
            hello.Channel,
            static (channelName, state) => new BrokerChannel(channelName, state.RequestedBufferBytes, state.SlotCount, state.BrokerDirectory, state.InstanceId),
            new ChannelSeed(hello.MaxBytes, ResolveSlotCount(), socketDirectory ?? throw new InvalidOperationException("Broker socket directory was not resolved."), brokerState.InstanceId));

        channel.Attach(session, hello.MaxBytes);
        sessions[sessionId] = session;
        Interlocked.Exchange(ref lastEmptyTicks, 0);
        PersistBrokerState();

        TinyIpcLogger.Info(
            "NativeHost",
            "AttachRingSending",
            "Sending broker ATTACH_RING frame to client.",
            ("channel", channel.ChannelName),
            ("requestedBufferBytes", hello.MaxBytes),
            ("effectiveBudgetBytes", channel.Sizing.BudgetBytes),
            ("budgetWasCapped", channel.Sizing.WasCapped),
            ("ringPath", channel.Ring.FilePath),
            ("slotCount", channel.Ring.SlotCount),
            ("slotPayloadBytes", channel.Ring.SlotPayloadBytes),
            ("ringLength", channel.Ring.Length),
            ("startSequence", channel.Ring.CaptureHead()),
            ("sessionId", sessionId),
            ("protocolVersion", 3));

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
    catch (Exception ex)
    {
        TinyIpcLogger.Error(
            "NativeHost",
            "RegisterSessionFailed",
            "Failed to register broker session during attach.",
            ex,
            ("channel", hello.Channel),
            ("requestedBufferBytes", hello.MaxBytes),
            ("slotCount", requestedSizing.SlotCount),
            ("effectiveBudgetBytes", requestedSizing.BudgetBytes),
            ("budgetWasCapped", requestedSizing.WasCapped),
            ("derivedSlotPayloadBytes", requestedSizing.SlotPayloadBytes),
            ("derivedImageSize", requestedSizing.ImageSize));
        throw;
    }
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
                {
                    publishChannel.Publish(session, frame.Payload.ToArray());
                }
                else
                {
                    TinyIpcLogger.Warning(
                        "NativeHost",
                        "PublishChannelMissing",
                        "Received a publish for a channel that no longer has a live broker ring.",
                        null,
                        ("sessionId", session.SessionId),
                        ("channel", session.Channel),
                        ("payloadLength", frame.Payload.Length));
                    session.MarkDead();
                    return;
                }
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
    PersistBrokerState();
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
    return int.TryParse(raw, out int value) && value >= 8 ? value : 64;
}

static TimeSpan ResolveIdleShutdownDelay()
{
    string? raw = Environment.GetEnvironmentVariable("TINYIPC_BROKER_IDLE_SHUTDOWN_MS");
    return int.TryParse(raw, out int value) && value >= 250
        ? TimeSpan.FromMilliseconds(value)
        : TimeSpan.FromSeconds(120);
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

static bool TryBindListener(Socket listener, string socketPath)
{
    try
    {
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        return true;
    }
    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
    {
        if (CanConnectToSocket(socketPath))
            return false;

        try
        {
            if (File.Exists(socketPath))
                File.Delete(socketPath);
        }
        catch
        {
        }

        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        return true;
    }
}

static bool CanConnectToSocket(string socketPath)
{
    try
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.ReceiveTimeout = 250;
        socket.SendTimeout = 250;
        socket.Connect(new UnixDomainSocketEndPoint(socketPath));
        return true;
    }
    catch
    {
        return false;
    }
}

void PersistBrokerState()
{
    lock (stateGate)
    {
        brokerState = brokerState with
        {
            SessionCount = sessions.Count,
            ChannelCount = channels.Count,
            LastUpdatedUtcTicks = DateTimeOffset.UtcNow.UtcTicks
        };

        BrokerStateSnapshot.Write(brokerStatePath, brokerState);
    }
}

sealed class BrokerChannel : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, BrokerSession> _sessions = new();
    private readonly string _ringPath;
    private long _publishCount;
    private long _overwriteCount;
    private long _peakRetainedDepth;

    public BrokerChannel(string channelName, int requestedBufferBytes, int slotCount, string brokerDirectory, string instanceId)
    {
        ChannelName = channelName;
        _ringPath = UnixSharedStorageHelpers.BuildSharedFilePath(brokerDirectory, $"{channelName}_{instanceId}", "broker-ring");
        Sizing = RingSizingPolicy.Compute(requestedBufferBytes, slotCount, BrokeredChannelRing.HeaderBytes, BrokeredChannelRing.SlotHeaderBytes);
        Ring = BrokeredChannelRing.Create(_ringPath, Sizing.SlotCount, Sizing.SlotPayloadBytes);
        TinyIpcLogger.Info(
            "NativeHost",
            "ChannelCreated",
            "Created a broker channel ring with derived sizing.",
            ("channel", ChannelName),
            ("requestedBufferBytes", Sizing.RequestedBufferBytes),
            ("effectiveBudgetBytes", Sizing.BudgetBytes),
            ("budgetWasCapped", Sizing.WasCapped),
            ("slotCount", Ring.SlotCount),
            ("slotPayloadBytes", Ring.SlotPayloadBytes),
            ("ringLength", Ring.Length),
            ("ringPath", Ring.FilePath));
    }

    public string ChannelName { get; }
    public RingSizing Sizing { get; }
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
            RingSizing requestedSizing = RingSizingPolicy.Compute(requestedMaxPayloadBytes, Ring.SlotCount, BrokeredChannelRing.HeaderBytes, BrokeredChannelRing.SlotHeaderBytes);
            if (requestedSizing.SlotPayloadBytes > Ring.SlotPayloadBytes || requestedSizing.ImageSize > Ring.Length)
            {
                throw new InvalidOperationException(
                    $"Existing brokered ring for channel '{ChannelName}' is smaller than requested. " +
                    $"existingSlotCount={Ring.SlotCount}, existingSlotPayloadBytes={Ring.SlotPayloadBytes}, existingImageSize={Ring.Length}; " +
                    $"requestedBufferBytes={requestedMaxPayloadBytes}, requestedSlotPayloadBytes={requestedSizing.SlotPayloadBytes}, requestedImageSize={requestedSizing.ImageSize}.");
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
        BrokeredRingPublishResult publishResult;

        lock (_gate)
        {
            publishResult = Ring.Publish(sender.SessionId, payload);
            Interlocked.Increment(ref _publishCount);
            UpdatePeakRetainedDepth(publishResult.RetainedDepth);
            if (publishResult.OverwroteOldest)
                Interlocked.Increment(ref _overwriteCount);
            recipients = _sessions.Values.Where(x => !x.IsDead).ToArray();
        }

        if (publishResult.OverwroteOldest)
        {
            TinyIpcLogger.Warning(
                "NativeHost",
                "RingOverwriteOccurred",
                "A broker channel overwrote its oldest retained message to admit a new publish.",
                null,
                ("channel", ChannelName),
                ("senderSessionId", sender.SessionId),
                ("payloadLength", payload.Length),
                ("slotCount", Ring.SlotCount),
                ("slotPayloadBytes", Ring.SlotPayloadBytes),
                ("headBefore", publishResult.HeadBefore),
                ("tailBefore", publishResult.TailBefore),
                ("headAfter", publishResult.HeadAfter),
                ("tailAfter", publishResult.TailAfter),
                ("retainedDepth", publishResult.RetainedDepth));
        }

        foreach (BrokerSession recipient in recipients)
        {
            try
            {
                SidecarProtocol.WriteNotify(recipient.Socket);
            }
            catch (Exception ex)
            {
                TinyIpcLogger.Warning(
                    "NativeHost",
                    "NotifyRecipientFailed",
                    "Failed to notify a broker session about newly published data.",
                    ex,
                    ("channel", ChannelName),
                    ("recipientSessionId", recipient.SessionId),
                    ("senderSessionId", sender.SessionId));
                recipient.MarkDead();
            }
        }
    }

    public void Dispose()
    {
        TinyIpcLogger.Info(
            "NativeHost",
            "ChannelLifetimeSummary",
            "Completed broker channel lifetime summary.",
            ("channel", ChannelName),
            ("requestedBufferBytes", Sizing.RequestedBufferBytes),
            ("effectiveBudgetBytes", Sizing.BudgetBytes),
            ("budgetWasCapped", Sizing.WasCapped),
            ("slotCount", Ring.SlotCount),
            ("slotPayloadBytes", Ring.SlotPayloadBytes),
            ("ringLength", Ring.Length),
            ("currentHead", Ring.CaptureHead()),
            ("currentTail", Ring.CaptureTail()),
            ("currentRetainedDepth", Ring.CaptureHead() - Ring.CaptureTail()),
            ("publishCount", Interlocked.Read(ref _publishCount)),
            ("overwriteCount", Interlocked.Read(ref _overwriteCount)),
            ("peakRetainedDepth", Interlocked.Read(ref _peakRetainedDepth)));
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

    private void UpdatePeakRetainedDepth(int retainedDepth)
    {
        long current = Volatile.Read(ref _peakRetainedDepth);
        while (retainedDepth > current)
        {
            long observed = Interlocked.CompareExchange(ref _peakRetainedDepth, retainedDepth, current);
            if (observed == current)
            {
                if (retainedDepth >= Math.Max(1, Ring.SlotCount * 3 / 4))
                {
                    TinyIpcLogger.Info(
                        "NativeHost",
                        "ChannelHighRetainedDepthObserved",
                        "Observed a high retained message depth for a broker channel.",
                        ("channel", ChannelName),
                        ("retainedDepth", retainedDepth),
                        ("slotCount", Ring.SlotCount),
                        ("slotPayloadBytes", Ring.SlotPayloadBytes),
                        ("publishCount", Interlocked.Read(ref _publishCount)));
                }

                return;
            }

            current = observed;
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

readonly record struct ChannelSeed(int RequestedBufferBytes, int SlotCount, string BrokerDirectory, string InstanceId);
