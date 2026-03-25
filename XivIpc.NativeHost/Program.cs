using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
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
if (!string.IsNullOrWhiteSpace(Environment.ProcessPath))
    UnixSharedStorageHelpers.ApplyHostExecutablePermissions(Environment.ProcessPath);
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
int shutdownRequested = 0;
Task monitorTask = Task.Run(() => MonitorSessionsAsync(shutdownCts.Token));

void RequestShutdown(string reason, Exception? exception = null)
{
    if (Interlocked.Exchange(ref shutdownRequested, 1) != 0)
        return;

    TinyIpcLogger.Info(
        "NativeHost",
        "ShutdownRequested",
        "Broker shutdown was requested.",
        ("reason", reason),
        ("socketPath", socketPath));

    if (exception is not null)
    {
        TinyIpcLogger.Warning(
            "NativeHost",
            "ShutdownRequestException",
            "Broker shutdown was triggered because an exception occurred in a shutdown pathway.",
            exception,
            ("reason", reason),
            ("socketPath", socketPath));
    }

    try
    {
        shutdownCts.Cancel();
    }
    catch (Exception ex)
    {
        TinyIpcLogger.Warning(
            "NativeHost",
            "ShutdownCancellationFailed",
            "Failed to cancel broker shutdown token.",
            ex,
            ("reason", reason),
            ("socketPath", socketPath));
    }

    try
    {
        listener.Dispose();
    }
    catch (Exception ex)
    {
        TinyIpcLogger.Warning(
            "NativeHost",
            "ListenerDisposeFailedDuringShutdown",
            "Failed to dispose the broker listener during shutdown.",
            ex,
            ("reason", reason),
            ("socketPath", socketPath));
    }
}

void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
{
    e.Cancel = true;
    RequestShutdown("ConsoleCancelKeyPress");
}

void OnUnloading(AssemblyLoadContext context)
{
    RequestShutdown("AssemblyLoadContextUnloading");
}

void OnProcessExit(object? sender, EventArgs e)
{
    RequestShutdown("ProcessExit");
}

using PosixSignalRegistration? sigIntRegistration =
    OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
        ? PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
        {
            context.Cancel = true;
            RequestShutdown("SIGINT");
        })
        : null;

using PosixSignalRegistration? sigTermRegistration =
    OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
        ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            RequestShutdown("SIGTERM");
        })
        : null;

Console.CancelKeyPress += OnCancelKeyPress;
AssemblyLoadContext.Default.Unloading += OnUnloading;
AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

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
        catch (Exception ex)
        {
            TinyIpcLogger.Error(
                "NativeHost",
                "AcceptFailed",
                "Broker accept loop failed while waiting for a new client connection.",
                ex,
                ("socketPath", socketPath));
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
            catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
            {
                TinyIpcLogger.Debug(
                    "NativeHost",
                    "SessionCanceledDuringShutdown",
                    "Broker session processing was canceled during shutdown.",
                    ("hasSession", session is not null),
                    ("sessionId", session?.SessionId ?? 0),
                    ("channel", session?.Channel ?? string.Empty),
                    ("ownerPid", session?.OwnerPid ?? 0));
            }
            catch (EndOfStreamException ex)
            {
                TinyIpcLogger.Warning(
                    "NativeHost",
                    "SessionDisconnectedDuringHandshakeOrRead",
                    "Broker client disconnected before completing handshake or frame processing.",
                    ex,
                    ("hasSession", session is not null),
                    ("sessionId", session?.SessionId ?? 0),
                    ("channel", session?.Channel ?? string.Empty),
                    ("ownerPid", session?.OwnerPid ?? 0),
                    ("peerUid", session?.PeerCredentials.Uid ?? 0),
                    ("peerGid", session?.PeerCredentials.Gid ?? 0));
            }
            catch (IOException ex)
            {
                TinyIpcLogger.Warning(
                    "NativeHost",
                    "SessionIoFailed",
                    "Broker session ended because of an I/O failure.",
                    ex,
                    ("hasSession", session is not null),
                    ("sessionId", session?.SessionId ?? 0),
                    ("channel", session?.Channel ?? string.Empty),
                    ("ownerPid", session?.OwnerPid ?? 0),
                    ("peerUid", session?.PeerCredentials.Uid ?? 0),
                    ("peerGid", session?.PeerCredentials.Gid ?? 0));
            }
            catch (ObjectDisposedException ex)
            {
                TinyIpcLogger.Debug(
                    "NativeHost",
                    "SessionSocketDisposed",
                    "Broker session ended because the socket was disposed.",
                    ("hasSession", session is not null),
                    ("sessionId", session?.SessionId ?? 0),
                    ("channel", session?.Channel ?? string.Empty),
                    ("ownerPid", session?.OwnerPid ?? 0),
                    ("exceptionType", ex.GetType().FullName ?? string.Empty),
                    ("exceptionMessage", ex.Message));
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
                    ("ownerPid", session?.OwnerPid ?? 0),
                    ("peerUid", session?.PeerCredentials.Uid ?? 0),
                    ("peerGid", session?.PeerCredentials.Gid ?? 0));

                try
                {
                    if (session is not null)
                        session.Write(static (s, message) => SidecarProtocol.WriteError(s, message), ex.Message);
                    else
                        SidecarProtocol.WriteError(socket, ex.Message);
                }
                catch (Exception writeEx)
                {
                    TinyIpcLogger.Debug(
                        "NativeHost",
                        "SessionErrorFrameWriteFailed",
                        "Failed to write an error frame after a session processing failure.",
                        ("exceptionType", writeEx.GetType().FullName ?? string.Empty),
                        ("exceptionMessage", writeEx.Message));
                }
            }
            finally
            {
                if (session is not null)
                    RemoveSession(session);

                try
                {
                    socket.Dispose();
                }
                catch (Exception ex)
                {
                    TinyIpcLogger.Debug(
                        "NativeHost",
                        "AcceptedSocketDisposeFailed",
                        "Disposing an accepted client socket failed during session cleanup.",
                        ("exceptionType", ex.GetType().FullName ?? string.Empty),
                        ("exceptionMessage", ex.Message));
                }
            }
        }, shutdownCts.Token);
    }
}
finally
{
    Console.CancelKeyPress -= OnCancelKeyPress;
    AssemblyLoadContext.Default.Unloading -= OnUnloading;
    AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

    try
    {
        shutdownCts.Cancel();
    }
    catch (Exception ex)
    {
        TinyIpcLogger.Warning(
            "NativeHost",
            "FinalShutdownCancellationFailed",
            "Failed to cancel broker shutdown token during final cleanup.",
            ex,
            ("socketPath", socketPath));
    }

    try
    {
        listener.Dispose();
    }
    catch (Exception ex)
    {
        TinyIpcLogger.Warning(
            "NativeHost",
            "FinalListenerDisposeFailed",
            "Failed to dispose broker listener during final cleanup.",
            ex,
            ("socketPath", socketPath));
    }

    try
    {
        await monitorTask.ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception ex)
    {
        TinyIpcLogger.Warning(
            "NativeHost",
            "MonitorTaskJoinFailed",
            "Failed while awaiting the broker monitor task during shutdown.",
            ex,
            ("socketPath", socketPath));
    }

    foreach (BrokerSession session in sessions.Values)
    {
        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            TinyIpcLogger.Warning(
                "NativeHost",
                "SessionDisposeFailedDuringShutdown",
                "Failed to dispose a broker session during final cleanup.",
                ex,
                ("sessionId", session.SessionId),
                ("channel", session.Channel),
                ("ownerPid", session.OwnerPid));
        }
    }

    foreach (BrokerChannel channel in channels.Values)
    {
        try
        {
            channel.Dispose();
        }
        catch (Exception ex)
        {
            TinyIpcLogger.Warning(
                "NativeHost",
                "ChannelDisposeFailedDuringShutdown",
                "Failed to dispose a broker channel during final cleanup.",
                ex,
                ("channel", channel.ChannelName));
        }
    }

    try
    {
        BrokerStateSnapshot? activeState = BrokerStateSnapshot.TryRead(brokerStatePath);
        if (activeState is not null &&
            string.Equals(activeState.InstanceId, brokerState.InstanceId, StringComparison.Ordinal) &&
            File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }
    }
    catch (Exception ex)
    {
        TinyIpcLogger.Warning(
            "NativeHost",
            "SocketCleanupFailedDuringShutdown",
            "Failed to remove the broker socket path during final cleanup.",
            ex,
            ("socketPath", socketPath));
    }

    try
    {
        BrokerStateSnapshot.DeleteIfOwned(brokerStatePath, brokerState.InstanceId);
    }
    catch (Exception ex)
    {
        TinyIpcLogger.Warning(
            "NativeHost",
            "BrokerStateCleanupFailedDuringShutdown",
            "Failed to clean up broker state snapshot during final cleanup.",
            ex,
            ("brokerStatePath", brokerStatePath),
            ("instanceId", brokerState.InstanceId));
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

    JournalSizing requestedSizing = JournalSizingPolicy.Compute(hello.MaxBytes, BrokeredChannelJournal.HeaderBytes);
    long sessionId = Interlocked.Increment(ref nextSessionId);
    Guid clientInstanceId = hello.ClientInstanceId == Guid.Empty ? Guid.NewGuid() : hello.ClientInstanceId;
    var session = new BrokerSession(sessionId, hello.Channel, hello.OwnerPid, hello.HeartbeatTimeoutMs, clientInstanceId, socket, peerCredentials);

    try
    {
        BrokerChannel channel = channels.GetOrAdd(
            hello.Channel,
            static (channelName, state) => new BrokerChannel(channelName, state.RequestedBufferBytes, state.BrokerDirectory, state.InstanceId, state.MinAgeMs),
            new ChannelSeed(hello.MaxBytes, socketDirectory ?? throw new InvalidOperationException("Broker socket directory was not resolved."), brokerState.InstanceId, ResolveMinMessageAgeMs()));

        channel.Attach(session, hello.MaxBytes);
        sessions[sessionId] = session;
        Interlocked.Exchange(ref lastEmptyTicks, 0);
        PersistBrokerState();

        TinyIpcLogger.Info(
            "NativeHost",
            "SessionAttached",
            "Registered a broker session for a sidecar client.",
            ("sessionId", sessionId),
            ("channel", hello.Channel),
            ("ownerPid", hello.OwnerPid),
            ("clientInstanceId", clientInstanceId),
            ("peerUid", peerCredentials.Uid),
            ("peerGid", peerCredentials.Gid),
            ("channelSessionCount", channel.SessionCount));

        TinyIpcLogger.Info(
            "NativeHost",
            "AttachRingSending",
            "Sending broker ATTACH_RING frame to client.",
            ("channel", channel.ChannelName),
            ("requestedBufferBytes", hello.MaxBytes),
            ("ownerPid", hello.OwnerPid),
            ("effectiveBudgetBytes", channel.Sizing.BudgetBytes),
            ("budgetWasCapped", channel.Sizing.WasCapped),
            ("ringPath", channel.Journal.FilePath),
            ("capacityBytes", channel.Journal.CapacityBytes),
            ("maxPayloadBytes", channel.Journal.MaxPayloadBytes),
            ("ringLength", channel.Journal.Length),
            ("startSequence", channel.Journal.CaptureHeadSequence()),
            ("sessionId", sessionId),
            ("protocolVersion", 4));

        session.Write(
            static (s, attach) => SidecarProtocol.WriteAttachRing(s, attach),
            new SidecarAttachRing(
                channel.Journal.FilePath,
                channel.Journal.CapacityBytes,
                channel.Journal.MaxPayloadBytes,
                channel.Journal.CaptureHeadSequence(),
                sessionId,
                channel.Journal.Length,
                4));

        session.Write(static s => SidecarProtocol.WriteReady(s));
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
            ("ownerPid", hello.OwnerPid),
            ("requestedBufferBytes", hello.MaxBytes),
            ("effectiveBudgetBytes", requestedSizing.BudgetBytes),
            ("budgetWasCapped", requestedSizing.WasCapped),
            ("derivedMaxPayloadBytes", requestedSizing.MaxPayloadBytes),
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
                session.Write(static (s, message) => SidecarProtocol.WriteError(s, message), $"Unexpected sidecar frame '{frame.Type}'.");
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
    int channelSessionCount = 0;

    if (channels.TryGetValue(session.Channel, out BrokerChannel? channel))
    {
        channel.Detach(session.SessionId);
        channelSessionCount = channel.SessionCount;
        if (channel.IsEmpty && channels.TryRemove(session.Channel, out BrokerChannel? removed))
            removed.Dispose();
    }

    if (sessions.IsEmpty)
        Interlocked.Exchange(ref lastEmptyTicks, DateTimeOffset.UtcNow.UtcTicks);

    session.Dispose();
    PersistBrokerState();

    TinyIpcLogger.Info(
        "NativeHost",
        "SessionDetached",
        "Removed a broker session for a sidecar client.",
        ("sessionId", session.SessionId),
        ("channel", session.Channel),
        ("ownerPid", session.OwnerPid),
        ("peerUid", session.PeerCredentials.Uid),
        ("peerGid", session.PeerCredentials.Gid),
        ("channelSessionCount", channelSessionCount));
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
                RequestShutdown("IdleTimeoutElapsed");
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

static long ResolveMinMessageAgeMs()
{
    string? raw = Environment.GetEnvironmentVariable("TINYIPC_MESSAGE_TTL_MS");
    return long.TryParse(raw, out long value) && value >= 0 ? value : 1_000L;
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
    private long _peakRetainedBytes;

    public BrokerChannel(string channelName, int requestedBufferBytes, string brokerDirectory, string instanceId, long minAgeMs)
    {
        ChannelName = channelName;
        _ringPath = UnixSharedStorageHelpers.BuildSharedFilePath(brokerDirectory, $"{channelName}_{instanceId}", "broker-journal");
        Sizing = JournalSizingPolicy.Compute(requestedBufferBytes, BrokeredChannelJournal.HeaderBytes);
        Journal = BrokeredChannelJournal.Create(_ringPath, Sizing.BudgetBytes, Sizing.MaxPayloadBytes, minAgeMs);
        TinyIpcLogger.Info(
            "NativeHost",
            "ChannelCreated",
            "Created a broker channel journal with derived sizing.",
            ("channel", ChannelName),
            ("requestedBufferBytes", Sizing.RequestedBufferBytes),
            ("effectiveBudgetBytes", Sizing.BudgetBytes),
            ("budgetWasCapped", Sizing.WasCapped),
            ("maxPayloadBytes", Journal.MaxPayloadBytes),
            ("journalLength", Journal.Length),
            ("journalPath", Journal.FilePath),
            ("minAgeMs", Journal.MinAgeMs));
    }

    public string ChannelName { get; }
    public JournalSizing Sizing { get; }
    public BrokeredChannelJournal Journal { get; }
    public bool IsEmpty
    {
        get
        {
            lock (_gate)
                return _sessions.Count == 0;
        }
    }

    public int SessionCount
    {
        get
        {
            lock (_gate)
                return _sessions.Count;
        }
    }

    public void Attach(BrokerSession session, int requestedMaxPayloadBytes)
    {
        lock (_gate)
        {
            JournalSizing requestedSizing = JournalSizingPolicy.Compute(requestedMaxPayloadBytes, BrokeredChannelJournal.HeaderBytes);
            if (requestedSizing.MaxPayloadBytes > Journal.MaxPayloadBytes || requestedSizing.ImageSize > Journal.Length)
            {
                throw new InvalidOperationException(
                    $"Existing brokered journal for channel '{ChannelName}' is smaller than requested. " +
                    $"existingCapacityBytes={Journal.CapacityBytes}, existingMaxPayloadBytes={Journal.MaxPayloadBytes}, existingImageSize={Journal.Length}; " +
                    $"requestedBufferBytes={requestedMaxPayloadBytes}, requestedMaxPayloadBytes={requestedSizing.MaxPayloadBytes}, requestedImageSize={requestedSizing.ImageSize}.");
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
        BrokeredJournalPublishResult publishResult;

        lock (_gate)
        {
            publishResult = Journal.Publish(sender.ClientInstanceId, payload);
            Interlocked.Increment(ref _publishCount);
            UpdatePeakRetainedBytes(publishResult.RetainedBytesAfter);
            recipients = _sessions.Values.Where(x => !x.IsDead).ToArray();
        }

        if (publishResult.PrunedExpired || publishResult.Compacted)
        {
            TinyIpcLogger.Info(
                "NativeHost",
                "JournalPrunedOrCompacted",
                "Broker journal pruned expired records or compacted retained bytes to admit a new publish.",
                ("channel", ChannelName),
                ("senderSessionId", sender.SessionId),
                ("senderInstanceId", sender.ClientInstanceId),
                ("senderOwnerPid", sender.OwnerPid),
                ("payloadLength", payload.Length),
                ("capacityBytes", Journal.CapacityBytes),
                ("maxPayloadBytes", Journal.MaxPayloadBytes),
                ("headBefore", publishResult.HeadSequenceBefore),
                ("tailBefore", publishResult.TailSequenceBefore),
                ("headAfter", publishResult.HeadSequenceAfter),
                ("tailAfter", publishResult.TailSequenceAfter),
                ("retainedBytesBefore", publishResult.RetainedBytesBefore),
                ("retainedBytesAfter", publishResult.RetainedBytesAfter),
                ("prunedExpired", publishResult.PrunedExpired),
                ("compacted", publishResult.Compacted));
        }

        if (TinyIpcLogger.IsEnabled(TinyIpcLogLevel.Debug))
        {
            TinyIpcLogger.Debug(
                "NativeHost",
                "PublishFanout",
                "Fanned out a broker publish to the current live session set.",
                ("channel", ChannelName),
                ("senderSessionId", sender.SessionId),
                ("senderInstanceId", sender.ClientInstanceId),
                ("senderOwnerPid", sender.OwnerPid),
                ("recipientCount", recipients.Length),
                ("recipientSessionIds", string.Join(",", recipients.Select(static x => x.SessionId))));
        }

        foreach (BrokerSession recipient in recipients)
        {
            try
            {
                recipient.Write(static s => SidecarProtocol.WriteNotify(s));
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
                    ("recipientOwnerPid", recipient.OwnerPid),
                    ("senderSessionId", sender.SessionId),
                    ("senderOwnerPid", sender.OwnerPid));
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
            ("maxPayloadBytes", Journal.MaxPayloadBytes),
            ("journalLength", Journal.Length),
            ("currentHead", Journal.CaptureHeadSequence()),
            ("currentTail", Journal.CaptureTailSequence()),
            ("currentRetainedBytes", Journal.CaptureHeadOffset() - Journal.CaptureTailOffset()),
            ("publishCount", Interlocked.Read(ref _publishCount)),
            ("peakRetainedBytes", Interlocked.Read(ref _peakRetainedBytes)));
        Journal.Dispose();
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

    private void UpdatePeakRetainedBytes(int retainedBytes)
    {
        long current = Volatile.Read(ref _peakRetainedBytes);
        while (retainedBytes > current)
        {
            long observed = Interlocked.CompareExchange(ref _peakRetainedBytes, retainedBytes, current);
            if (observed == current)
            {
                if (retainedBytes >= Math.Max(1, Journal.CapacityBytes * 3 / 4))
                {
                    TinyIpcLogger.Info(
                        "NativeHost",
                        "ChannelHighRetainedBytesObserved",
                        "Observed a high retained byte depth for a broker channel.",
                        ("channel", ChannelName),
                        ("retainedBytes", retainedBytes),
                        ("capacityBytes", Journal.CapacityBytes),
                        ("maxPayloadBytes", Journal.MaxPayloadBytes),
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

    public BrokerSession(long sessionId, string channel, int ownerPid, int heartbeatTimeoutMs, Guid clientInstanceId, Socket socket, LinuxBrokerInterop.PeerCredentials peerCredentials)
    {
        SessionId = sessionId;
        Channel = channel;
        OwnerPid = ownerPid;
        HeartbeatTimeoutMs = heartbeatTimeoutMs > 0 ? heartbeatTimeoutMs : 60000;
        ClientInstanceId = clientInstanceId;
        Socket = socket;
        PeerCredentials = peerCredentials;
        LastHeartbeatTicks = DateTimeOffset.UtcNow.UtcTicks;
    }

    public long SessionId { get; }
    public string Channel { get; }
    public int OwnerPid { get; }
    public int HeartbeatTimeoutMs { get; }
    public Guid ClientInstanceId { get; }
    public Socket Socket { get; }
    public object WriteGate { get; } = new();
    public LinuxBrokerInterop.PeerCredentials PeerCredentials { get; }
    public long LastHeartbeatTicks;
    public bool IsDead => Volatile.Read(ref _dead) != 0;

    public void Write(Action<Socket> writeAction)
    {
        ArgumentNullException.ThrowIfNull(writeAction);

        lock (WriteGate)
        {
            if (IsDead)
                return;

            writeAction(Socket);
        }
    }

    public void Write<TState>(Action<Socket, TState> writeAction, TState state)
    {
        ArgumentNullException.ThrowIfNull(writeAction);

        lock (WriteGate)
        {
            if (IsDead)
                return;

            writeAction(Socket, state);
        }
    }

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

readonly record struct ChannelSeed(int RequestedBufferBytes, string BrokerDirectory, string InstanceId, long MinAgeMs);
